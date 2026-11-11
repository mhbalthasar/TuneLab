using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SDL2;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;

namespace TuneLab.Audio.SDL2;

internal class SDLPlaybackHandler : IAudioPlaybackHandler, ISecondaryDeviceHost, ISecondaryAudioSink
{
    public event Action? PlayStateChanged;
    public event Action? ProgressChanged;
    public event Action? CurrentDeviceChanged;
    public event Action? DevicesChanged;

    public bool IsPlaying => _d.state == SDLPlaybackData.PlaybackState.Playing;

    static string AutoDeviceName => IAudioPlaybackHandler.AutoDeviceName;

    // 当前音频驱动
    public string CurrentDriver
    {
        get
        {
            if (!_initialized)
            {
                return _cachedArguments.CurrentDriver == null ? string.Empty : _cachedArguments.CurrentDriver;
            }

            return _d.driver;
        }
        set
        {
            if (!_initialized)
            {
                _cachedArguments.CurrentDriver = value;
                return;
            }

            if (_d.driver == value)
            {
                return;
            }

            // 副设备挂在当前驱动上：换驱动前必须全部关掉（SDL_AudioQuit 之后它们就是野句柄）。
            CloseSecondaryDevices();

            _d.SetDriver(value); // 请传入合法的音频驱动
        }
    }

    // 当前音频设备（需要经过名称转换）
    public string CurrentDevice
    {
        get
        {
            if (!_initialized)
            {
                return _cachedArguments.CurrentDevice == null ? string.Empty : _cachedArguments.CurrentDevice;
            }

            if (_d.devName == SDLGlobal.PLAYBACK_EMPTY_DEVICE)
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(_d.devName))
            {
                return AutoDeviceName;
            }

            return _d.devName;
        }
        set
        {
            if (!_initialized)
            {
                _cachedArguments.CurrentDevice = value;
                return;
            }

            if (string.IsNullOrEmpty(value))
            {
                value = SDLGlobal.PLAYBACK_EMPTY_DEVICE;
            }
            else if (value == AutoDeviceName)
            {
                value = string.Empty;
            }

            if (_d.devName == value)
            {
                return;
            }

            Stop();

            // 主设备要换：副设备的 spec/队列都跟旧主设备绑着，全部关掉（下一次编图重开）。
            CloseSecondaryDevices();

            _d.SetDevice(value);
        }
    }

    public int BufferSize
    {
        get => _d.spec.samples;
        set
        {
            if (!_initialized)
            {
                if (value > 0)
                    _d.spec.samples = (ushort)value;
                return;
            }

            // 块长变了：副设备的暂存缓冲/水位都按旧块长算，必须关掉重开。
            CloseSecondaryDevices();
            ResetAudioSpec((ushort)value, null, null);
        }
    }

    public int SampleRate
    {
        get => _d.spec.freq;
        set
        {
            if (!_initialized)
            {
                if (value > 0)
                    _d.spec.freq = value;
                return;
            }

            // 采样率变了：副设备必须按新 freq 重开（这一版不做重采样）。
            CloseSecondaryDevices();
            ResetAudioSpec(null, value, null);
        }
    }

    public int ChannelCount
    {
        get => _d.spec.channels;
        set
        {
            if (!_initialized)
            {
                if (value > 0)
                    _d.spec.channels = (byte)value;
                return;
            }

            // 声道数变了：副设备必须按新格式重开。
            CloseSecondaryDevices();
            ResetAudioSpec(null, null, (byte)value);
        }
    }

    public void Init(IAudioSampleProvider provider)
    {
        var context = SynchronizationContext.Current;
        if (context == null)
        {
            throw new Exception("SDL: failed to get SynchronizationContext");
        }

        _audioProvider = provider;

        // Begin initialize SDL
        {
            // 初始化SDL引擎，有备无患
            _ = SDLHost.InitHost();

            // 转发事件：设备更改
            _d.onDevChanged = (newVal, oldVal) =>
            {
                context.Post(_ =>
                {
                    CurrentDeviceChanged?.Invoke(); //
                }, null);
            };
            // 转发事件：播放状态更改
            _d.onStateChanged = (newVal, oldVal) =>
            {
                context.Post(_ =>
                {
                    PlayStateChanged?.Invoke(); //
                }, null);
            };
            // 转发事件：设备列表更新
            _d.onDevicesUpdated = () =>
            {
                context.Post(_ =>
                {
                    // 设备变动后重新检测音频设备
                    SDL.SDL_GetNumAudioDevices(0);

                    DevicesChanged?.Invoke(); //
                }, null);
            };
            // [EP-MIXER-31] 转发事件：当前缓冲区被播放
            _d.onSamplesConsumed = playedFrames =>
            {
                // 在音频回调线程上更新"听到的位置"（不碰 UI）。
                _audioProvider.OnSamplesConsumed(playedFrames);
                context.Post(_ =>
                {
                    if (IsPlaying)
                    {
                        ProgressChanged?.Invoke();
                    }
                }, null);
            };
            // 填充缓冲区
            _d.onBufferEnd = (buffer, offset, count) =>
            {
                // [EP-MIXER-31] 音频优先：这段跑在 SDL 的音频回调线程上。第一次进来就把它抬到
                // Highest（进程本身在 AudioEngine.Init 里已抬到 High）。设备重启会
                // 换线程，所以按线程 id 判一次；受限账户上设置失败不影响出声。
                int threadId = Environment.CurrentManagedThreadId;
                try
                {
                    // 线程 id 可能被运行时复用（设备重启后新线程拿到旧 id），
                    // 所以除了 id 还核对一次当前优先级。
                    if (sBoostedAudioThreadId != threadId || Thread.CurrentThread.Priority != ThreadPriority.Highest)
                    {
                        sBoostedAudioThreadId = threadId;
                        Thread.CurrentThread.Priority = ThreadPriority.Highest;
                    }
                }
                catch (Exception) { }

                int length = count / 2;
                for (int i = offset; i < offset + count; i++)
                {
                    buffer[i] = 0;
                }

                _audioProvider.Read(buffer, offset, length);
                return count;
            };

            // 打开默认音频设备延迟到播放操作时
        }
        // End initialize SDL
        _initialized = true;

        if (_cachedArguments.CurrentDriver != null)
        {
            CurrentDriver = _cachedArguments.CurrentDriver;
        }
        else
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                CurrentDriver = "directsound";
            }
        }

        if (_cachedArguments.CurrentDevice != null)
        {
            CurrentDevice = _cachedArguments.CurrentDevice;
        }
    }

    public void Destroy()
    {
        // 先关闭音频
        Stop();
        CloseSecondaryDevices();

        _d.SetDevice(SDLGlobal.PLAYBACK_EMPTY_DEVICE);
        _d.SetDriver(string.Empty);

        _initialized = false;
    }

    private void ResetAudioSpec(ushort? samples, int? sampleRate, byte? channels)
    {
        var curDevice = _d.devName;
        var isPlaying = IsPlaying;

        Stop();
        if (curDevice != SDLGlobal.PLAYBACK_EMPTY_DEVICE)
        {
            _d.SetDevice(SDLGlobal.PLAYBACK_EMPTY_DEVICE);
        }

        if (samples.HasValue && samples.Value > 0)
            _d.spec.samples = samples.Value;
        if (sampleRate.HasValue && sampleRate.Value > 0)
            _d.spec.freq = sampleRate.Value;
        if (channels.HasValue && channels.Value > 0)
            _d.spec.channels = channels.Value;

        if (curDevice != SDLGlobal.PLAYBACK_EMPTY_DEVICE)
        {
            _d.SetDevice(curDevice);
        }

        if (isPlaying)
        {
            Start();
        }
    }

    public void Start()
    {
        if (IsPlaying)
        {
            return;
        }

        // 如果没有打开音频设备那么打开默认音频设备
        if (_d.devId == 0)
        {
            CurrentDevice = AutoDeviceName;
        }

        // 先让副设备就位（取消暂停 + 垫一块静音），再启动主设备的生产者线程：
        // 否则第一块 Push 可能落在"副设备还暂停着"的窗口里，被随后的 Flush 清掉。
        StartSecondaryDevices();
        _d.Start();
    }

    public void Stop()
    {
        if (!IsPlaying)
        {
            return;
        }

        _d.Stop();
        StopSecondaryDevices();
    }

    // [EP-MIXER-31] 丢弃已填充但未播完的块（暂停/恢复/跳转）。必须让生产者重新填一块，
    // 否则下一个回调看到空块只会输出静音，不会推 BUFFER_END。
    public void Flush()
    {
        _d.Flush();

        // 暂停/恢复/跳转：副设备队列里的旧块同样要丢，否则会滞后一个缓冲。
        FlushSecondaryDevices();
    }

    // 获取所有音频设备
    public IReadOnlyList<string> GetAllDevices()
    {
        var res = new List<string>
        {
            AutoDeviceName
        };

        int cnt = SDL.SDL_GetNumAudioDevices(0);
        if (cnt <= 0)
        {
            // [EP-MIXER-31] 设备枚举为空时把驱动和 SDL 错误写进日志：用户看到的"只有 (Auto)"通常就是这里。
            Log.Warning($"SDL: no output audio devices (driver='{_d.driver}', error='{SDL.SDL_GetError()}')");
            return res;
        }

        for (int i = 0; i < cnt; i++)
        {
            var name = SDL.SDL_GetAudioDeviceName(i, 0);
            if (string.IsNullOrEmpty(name))
                continue;
            if (!res.Contains(name))
                res.Add(name);
        }

        return res;
    }

    // ── 多设备输出（副设备）──
    //
    // 主设备（_d）仍是唯一的时钟主：它按自己的回调节拍渲染每一块，副设备只从同一块里
    // 取走自己那条设备流（见 AudioSampleProvider.ReadGraph）。因此：
    //   · 插件只跑一遍，不会因为多开一个设备而翻倍；
    //   · 副设备与主设备共用播放头，不存在"两条流各自走"的内容漂移；
    //   · 两个设备的**采样时钟**仍然独立（ppm 级漂移），靠 SDL 队列深度做块级丢补样。
    //     DESIGN.md §4.4 v2 允许监听端有极少量丢补样；主输出完全不受影响。
    //
    // 副设备用 SDL_QueueAudio 喂（回调为 null）：SDL 会把数据拷贝进内部队列，
    // 所以暂存缓冲可以复用，音频线程零分配。

    readonly object mSecondaryLock = new();
    readonly Dictionary<string, SecondaryOutput> mSecondary = new(StringComparer.Ordinal);
    volatile string[] mSecondaryIds = [];

    sealed class SecondaryOutput
    {
        public required string Name;
        public required uint DeviceId;
        public required SDL.SDL_AudioSpec Spec;
        public required float[] Buffer;
        public required GCHandle Pinned;
        public required IntPtr Pointer;

        // 主设备一块的字节数：队列水位按它算（与副设备自己的 samples 无关）。
        public required uint BlockBytes;

        // 打开失败 / 队列写入失败（设备被拔掉、被独占）。下一次编图会关掉重开。
        public bool Failed;

        // 丢补样计数（只增不减，诊断用）。
        public long Dropped;
        public long Inserted;
    }

    public IReadOnlyList<string> DeviceIds => mSecondaryIds;

    public float[]? GetBuffer(string deviceId)
    {
        lock (mSecondaryLock)
        {
            return mSecondary.TryGetValue(deviceId, out var output) && !output.Failed
                ? output.Buffer
                : null;
        }
    }

    public void Push(string deviceId, int samples)
    {
        if (samples <= 0)
            return;

        lock (mSecondaryLock)
        {
            if (!mSecondary.TryGetValue(deviceId, out var output) || output.Failed)
                return;

            uint bytes = (uint)(samples * 2 * sizeof(float));
            uint queued = SDL.SDL_GetQueuedAudioSize(output.DeviceId);

            // 副设备比主设备慢：队列积到 4 块以上就丢掉这一块，把延迟拉回来。
            // （漂移是 ppm 级，正常播放几分钟才发生一次；监听端可接受。）
            if (queued > output.BlockBytes * 4)
            {
                output.Dropped++;
                return;
            }

            if (SDL.SDL_QueueAudio(output.DeviceId, output.Pointer, bytes) < 0)
            {
                // 设备被拔掉 / 被独占：标记失败，下一次编图会关掉重开（或回落 Master）。
                output.Failed = true;
                return;
            }

            // 副设备比主设备快：队列见底了，再补一块（否则下一块到来前会有一段静音）。
            if (queued == 0)
            {
                output.Inserted++;
                SDL.SDL_QueueAudio(output.DeviceId, output.Pointer, bytes);
            }
        }
    }

    public IReadOnlyCollection<string> SetSecondaryDevices(IReadOnlyCollection<string> deviceNames)
    {
        var failed = new List<string>();

        if (!_initialized)
        {
            // SDL 音频子系统还没起来：全部报失败，下一次编图再试。
            foreach (var name in deviceNames)
                failed.Add(name);
            return failed;
        }

        // 【锁的边界】打开 / 关闭设备都可能阻塞几十毫秒（WASAPI 初始化、驱动握手）。
        // 绝不能持着 mSecondaryLock 做这些事：音频线程每块都要拿它取缓冲/排队，
        // 持锁打开设备 = 音频回调被钉住 = 爆音。所以：
        //   1) 锁内只做字典增删（让音频线程立刻看不到要关的设备）；
        //   2) 关设备、开设备都在锁外做；
        //   3) 新设备开好后再进字典，音频线程下一次 ReadGraph 才开始喂它。

        var toClose = new List<SecondaryOutput>();

        lock (mSecondaryLock)
        {
            foreach (var (name, output) in mSecondary.ToArray())
            {
                if (!deviceNames.Contains(name) || output.Failed)
                {
                    toClose.Add(output);
                    mSecondary.Remove(name);
                }
            }

            mSecondaryIds = [.. mSecondary.Keys];
        }

        foreach (var output in toClose)
            CloseSecondary(output);

        var opened = new List<SecondaryOutput>();
        foreach (var name in deviceNames)
        {
            // 字典只有 UI 线程写；这里读不需要锁（音频线程只读）。
            if (mSecondary.ContainsKey(name) || opened.Any(o => o.Name == name))
                continue;

            if (TryOpenSecondary(name, out var output))
                opened.Add(output);
            else
                failed.Add(name);
        }

        if (opened.Count > 0)
        {
            lock (mSecondaryLock)
            {
                foreach (var output in opened)
                    mSecondary[output.Name] = output;

                mSecondaryIds = [.. mSecondary.Keys];
            }
        }

        return failed;
    }

    bool TryOpenSecondary(string name, out SecondaryOutput output)
    {
        output = null!;

        // 主设备没打开时不知道它实际接受的 freq/format；副设备必须与主设备同格式
        // （不做重采样），所以先不开 —— 下一次编图（主设备已开）再试。
        if (_d.devId == 0 || _d.spec.samples == 0)
            return false;

        var spec = _d.spec;
        spec.callback = null;              // 队列模式：SDL_QueueAudio 喂
        spec.userdata = IntPtr.Zero;
        spec.silence = 0;

        const int SDL_AUDIO_ALLOW_SAMPLES_CHANGE = 0x8;

        uint id = SDL.SDL_OpenAudioDevice(name, 0, ref spec, out var obtained, 0);
        if (id == 0)
        {
            id = SDL.SDL_OpenAudioDevice(name, 0, ref spec, out obtained, SDL_AUDIO_ALLOW_SAMPLES_CHANGE);
        }

        if (id == 0)
        {
            Log.Warning($"SDL: failed to open secondary output '{name}': {SDL.SDL_GetError()}");
            return false;
        }

        // 只允许块长不同。采样率/格式/声道变了就得重采样，这一版不做。
        if (obtained.freq != _d.spec.freq || obtained.channels != _d.spec.channels || obtained.format != _d.spec.format)
        {
            Log.Warning($"SDL: secondary output '{name}' has incompatible spec " +
                $"({obtained.freq} Hz, {obtained.channels} ch, {obtained.format}) vs primary " +
                $"({_d.spec.freq} Hz, {_d.spec.channels} ch, {_d.spec.format}); not used");
            SDL.SDL_CloseAudioDevice(id);
            return false;
        }

        int capacity = Math.Max(Mixer.DefaultMaxBlockSize, (int)_d.spec.samples) * 2;
        var buffer = new float[capacity];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        output = new SecondaryOutput
        {
            Name = name,
            DeviceId = id,
            Spec = obtained,
            Buffer = buffer,
            Pinned = pinned,
            Pointer = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0),
            BlockBytes = (uint)(_d.spec.samples * 2 * sizeof(float)),
        };

        SDL.SDL_PauseAudioDevice(id, 0);
        PrefillSecondary(output);

        Log.Info($"SDL: opened secondary output '{name}' (id={id}, {obtained.freq} Hz, {obtained.channels} ch, {obtained.samples} samples)");
        return true;
    }

    // 队列里先垫一块静音：给副设备留一个块的余量，避免"刚播完、下一块还没到"的每块空隙。
    // 代价是副设备比主设备晚一块（监听端可接受，且比掉音好）。
    static void PrefillSecondary(SecondaryOutput output)
    {
        int frames = Math.Min(output.Spec.samples, output.Buffer.Length / 2);
        if (frames <= 0)
            return;

        Array.Clear(output.Buffer, 0, frames * 2);
        SDL.SDL_QueueAudio(output.DeviceId, output.Pointer, (uint)(frames * 2 * sizeof(float)));
    }

    static void CloseSecondary(SecondaryOutput output)
    {
        SDL.SDL_ClearQueuedAudio(output.DeviceId);
        SDL.SDL_PauseAudioDevice(output.DeviceId, 1);
        SDL.SDL_CloseAudioDevice(output.DeviceId);

        if (output.Pinned.IsAllocated)
            output.Pinned.Free();
    }

    void CloseSecondaryDevices()
    {
        // 同样把"关设备"放到锁外：先让音频线程看不到它们，再逐个关闭。
        SecondaryOutput[] toClose;

        lock (mSecondaryLock)
        {
            toClose = [.. mSecondary.Values];
            mSecondary.Clear();
            mSecondaryIds = [];
        }

        foreach (var output in toClose)
            CloseSecondary(output);
    }

    void FlushSecondaryDevices()
    {
        lock (mSecondaryLock)
        {
            foreach (var output in mSecondary.Values)
            {
                SDL.SDL_ClearQueuedAudio(output.DeviceId);
                PrefillSecondary(output);
            }
        }
    }

    void StartSecondaryDevices()
    {
        lock (mSecondaryLock)
        {
            foreach (var output in mSecondary.Values)
            {
                SDL.SDL_PauseAudioDevice(output.DeviceId, 0);
                PrefillSecondary(output);
            }
        }
    }

    void StopSecondaryDevices()
    {
        lock (mSecondaryLock)
        {
            foreach (var output in mSecondary.Values)
            {
                SDL.SDL_ClearQueuedAudio(output.DeviceId);
                SDL.SDL_PauseAudioDevice(output.DeviceId, 1);
            }
        }
    }

    // 获取所有音频驱动
    public IReadOnlyList<string> GetAllDrivers()
    {
        var res = new List<string>();
        int cnt = SDL.SDL_GetNumAudioDrivers();
        for (int i = 0; i < cnt; i++)
        {
            var dev = SDL.SDL_GetAudioDriver(i);
            if (dev == "dummy" || dev == "disk")
            {
                continue;
            }

            res.Add(dev);
        }

        return res;
    }

    // 成员变量
    private IAudioSampleProvider _audioProvider;

    struct CachedArguments
    {
        public string? CurrentDriver;
        public string? CurrentDevice;

        public CachedArguments()
        {
            CurrentDriver = null;
            CurrentDevice = null;
        }
    }

    // [EP-MIXER-31] 已抬过优先级的音频回调线程 id（设备重启会换线程，故不是 bool）。
    static int sBoostedAudioThreadId;

    private bool _initialized = false;
    private CachedArguments _cachedArguments = new();

    // SDL 相关
    private SDLPlaybackData _d = new();
}