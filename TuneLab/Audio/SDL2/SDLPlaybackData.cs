using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SDL2;
using NAudio.Wave;
using TuneLab.Foundation;

namespace TuneLab.Audio.SDL2;

internal class SDLPlaybackData
{
    public enum PlaybackState
    {
        Stopped,
        Playing,
        Paused,
    }

    // 事件委托
    public SDLGlobal.ValueChangeEvent<string>? onDevChanged;

    public SDLGlobal.ValueChangeEvent<PlaybackState>? onStateChanged;

    // [EP-MIXER-31] 回调前累计的帧数，供宿主把“听到的位置”映射回工程时间轴。
    public SDLGlobal.ValueEvent<long>? onSamplesConsumed;

    public SDLGlobal.VoidEvent? onDevicesUpdated;

    public delegate int BufferEndEvent(float[] buffer, int offset, int count);

    public BufferEndEvent onBufferEnd;

    // 播放信息
    public string driver = string.Empty;

    public PlaybackState state = PlaybackState.Stopped;

    // 当前播放设备
    // (a) 0：无设备
    // (b) 非零：递增的设备 ID
    public uint devId;

    // 当前播放设备名
    // (a) 空：自动
    // (b) SDLGlobal.PLAYBACK_EMPTY_DEVICE：无设备
    // (c) 其他：指定的其他设备
    public string devName = SDLGlobal.PLAYBACK_EMPTY_DEVICE;

    public SDL.SDL_AudioSpec spec;

    // [EP-MIXER-31] 已写入设备流的帧数（不含正在写的这一块）。播放头用它对齐"听到的位置"。
    public long consumedFrames;

    // 线程相关
    private Thread producer;

    private Mutex mutex;

    // 控制块
    private struct CallbackBlock
    {
        public IntPtr audio_chunk;
        public int audio_len;
        public int audio_pos;
    }

    private CallbackBlock scb;

    private float[] pcm_buffer;

    // 构造函数
    public SDLPlaybackData()
    {
        // 默认音频配置
        spec.samples = 1024;
        spec.freq = 44100;
        spec.channels = 2;

        spec.userdata = IntPtr.Zero; // 不使用
        spec.callback = AudioSpecCallback;
        spec.format = SDLGlobal.PLAYBACK_FORMAT;
        spec.silence = 0;

        pcm_buffer = null;
        mutex = new Mutex();
    }

    public void Start()
    {
        SetState(PlaybackState.Playing);

        // 初始化控制块
        scb.audio_chunk = IntPtr.Zero;
        scb.audio_len = 0;
        scb.audio_pos = 0;

        // 启动生产者线程
        producer = new Thread(AudioLoop);
        producer.Start();
    }

    public void Stop()
    {
        // 结束生产者线程
        NotifyStop();

        // 等待结束
        producer.Join();
        producer = null;

        SetState(PlaybackState.Stopped);
    }

    public void SetDriver(string drv)
    {
        if (!string.IsNullOrEmpty(driver))
        {
            if (devId > 0)
            {
                // 停止播放
                if (state == PlaybackState.Playing)
                {
                    Stop();
                }

                SDL.SDL_CloseAudioDevice(devId); // 关闭音频设备

                devId = 0;
                devName = SDLGlobal.PLAYBACK_EMPTY_DEVICE;
            }

            // 关闭上一个驱动
            SDL.SDL_AudioQuit();
        }

        if (!string.IsNullOrEmpty(drv))
        {
            // 打开当前驱动
            if (SDL.SDL_AudioInit(drv) < 0)
            {
                throw new IOException($"SDL: failed to open audio driver: {SDL.SDL_GetError()}");
            }

            // 重新检测音频设备
            int deviceCount = SDL.SDL_GetNumAudioDevices(0);
            Log.Info($"SDL: audio driver '{drv}' initialized, {deviceCount} output device(s)");
            onDevicesUpdated?.Invoke();
        }

        driver = drv;
    }

    public void SetDevice(string dev)
    {
        var orgId = devId;
        var orgName = devName;

        if (orgId > 0)
        {
            // 停止播放
            if (state == PlaybackState.Playing)
            {
                Stop();
            }

            // 关闭上一个设备
            SDL.SDL_CloseAudioDevice(orgId);
        }

        // 先回到"无设备"：下面打开失败时不能留下一个已关闭的 devId。
        devId = 0;
        devName = SDLGlobal.PLAYBACK_EMPTY_DEVICE;

        if (dev != SDLGlobal.PLAYBACK_EMPTY_DEVICE)
        {
            // [EP-MIXER-31] 打开当前设备。先要求精确匹配宿主请求的块长；部分 WASAPI 设备只接受
            // 自己的块长（例如 441 帧），这时允许 SDL 改 samples 再试一次。
            // 只允许 samples 变化：格式/采样率/声道变了宿主就得重采样，这里不做。
            const int SDL_AUDIO_ALLOW_SAMPLES_CHANGE = 0x8;

            uint id;
            if ((id = SDL.SDL_OpenAudioDevice(
                    string.IsNullOrEmpty(dev) ? null : dev,
                    0,
                    ref spec,
                    out var obtained,
                    0)) == 0)
            {
                string firstError = SDL.SDL_GetError();
                if ((id = SDL.SDL_OpenAudioDevice(
                        string.IsNullOrEmpty(dev) ? null : dev,
                        0,
                        ref spec,
                        out obtained,
                        SDL_AUDIO_ALLOW_SAMPLES_CHANGE)) == 0)
                {
                    throw new IOException($"SDL: failed to open audio device \"{dev}\": {SDL.SDL_GetError()} (exact spec failed: {firstError})");
                }

                Log.Info($"SDL: device '{dev}' rejected the exact spec; opened with {obtained.samples} samples instead of {spec.samples}.");
            }

            // 用设备实际接受的 spec 填生产者缓冲区。只允许 samples 变化，
            // 所以 freq/format/channels 仍是宿主请求值；把 obtained 整体接过来最稳。
            spec = obtained;
            devId = id;
            devName = dev;
            consumedFrames = 0;

            // 初始化临时浮点数组
            var cnt = spec.samples * spec.channels;
            if (cnt == 0)
            {
                pcm_buffer = null;
            }
            else if (pcm_buffer == null || cnt != pcm_buffer.Length)
            {
                pcm_buffer = new float[cnt];
            }

            Log.Info($"SDL: opened audio device '{(string.IsNullOrEmpty(dev) ? "<default>" : dev)}' (id={id}, {spec.freq} Hz, {spec.channels} ch, {spec.samples} samples)");
        }

        // 通知音频设备已更改
        if (orgName != devName)
        {
            onDevChanged?.Invoke(devName, orgName);
        }
    }

    public void SetState(PlaybackState newState)
    {
        var orgState = state;
        state = newState;

        // 通知播放状态已更改
        if (state != orgState)
        {
            onStateChanged?.Invoke(state, orgState);
        }
    }

    // [EP-MIXER-31] 丢弃当前块并让生产者重新填一块。调用方在暂停/恢复/跳转时用。
    public void Flush()
    {
        mutex.WaitOne();
        scb.audio_len = 0;
        scb.audio_pos = 0;
        mutex.ReleaseMutex();

        // 生产者只在收到 BUFFER_END 时填块；清空后必须再推一次，否则会一直输出静音。
        NotifyAudioBufferEnd();
    }

    // 消费者
    private void AudioSpecCallback(IntPtr udata, IntPtr stream, int len)
    {
        // 上锁
        mutex.WaitOne();

        // 缓冲区置为静音
        SDLReimpl.SDL_memset(stream, 0, (IntPtr)len);

        if (scb.audio_len > 0 && scb.audio_chunk != IntPtr.Zero)
        {
            len = Math.Min(len, scb.audio_len);

            // [EP-MIXER-31] 回调前累计的帧数 = 设备已经播完的位置（这一块还没开始播）。
            long playedFrames = consumedFrames;

            // 将缓冲区中的声音写入流
            SDL.SDL_MixAudioFormat(
                stream,
                IntPtr.Add(scb.audio_chunk, scb.audio_pos),
                spec.format,
                (uint)len,
                SDL.SDL_MIX_MAXVOLUME
            );

            scb.audio_pos += len;
            scb.audio_len -= len;

            int frames = len / sizeof(float) / spec.channels;
            consumedFrames += frames;
            onSamplesConsumed?.Invoke(playedFrames);

            // 判断是否完毕
            if (scb.audio_len == 0)
            {
                NotifyAudioBufferEnd();
            }
        }

        // 放锁
        mutex.ReleaseMutex();
    }

    // 通知缓冲区已空
    private static void NotifyAudioBufferEnd()
    {
        var e = new SDL.SDL_Event();
        e.type = (SDL.SDL_EventType)SDLGlobal.UserEvent.SDL_EVENT_BUFFER_END;
        SDL.SDL_PushEvent(ref e);
    }

    // 通知暂停
    private static void NotifyStop()
    {
        var e = new SDL.SDL_Event();
        e.type = (SDL.SDL_EventType)SDLGlobal.UserEvent.SDL_EVENT_MANUAL_STOP;
        SDL.SDL_PushEvent(ref e);
    }

    // 生产者
    private void AudioLoop()
    {
        // 固定缓冲区
        var gch = GCHandle.Alloc(pcm_buffer, GCHandleType.Pinned);

        // 设置暂停标识位
        SDL.SDL_PauseAudioDevice(devId, 0);

        // 第一次事件
        NotifyAudioBufferEnd();

        // 外层循环
        bool devicesChanged = false;
        while (true)
        {
            bool over = false;

            // 不停地获取事件
            while (SDL.SDL_PollEvent(out var e) > 0)
            {
                switch ((int)e.type)
                {
                    // 缓存用完
                    case (int)SDLGlobal.UserEvent.SDL_EVENT_BUFFER_END:
                    {
                        // 上锁
                        mutex.WaitOne();

                        // 从文件中读取数据，剩下的就交给音频设备去完成了
                        // 它播放完一段数据后会执行回调函数，获取等多的数据
                        int samples = onBufferEnd(pcm_buffer, 0, spec.samples * spec.channels);
                        if (samples <= 0)
                        {
                            // 播放完毕
                            over = true;
                        }
                        else
                        {
                            // 重置缓冲区
                            scb.audio_chunk = Marshal.UnsafeAddrOfPinnedArrayElement(pcm_buffer, 0);
                            scb.audio_len = samples * sizeof(float); // 长度为读出数据长度，在read_audio_data中做减法
                            scb.audio_pos = 0; // 设置当前位置为缓冲区头部
                        }

                        // 放锁
                        mutex.ReleaseMutex();
                        break;
                    }

                    // 停止命令
                    case (int)SDLGlobal.UserEvent.SDL_EVENT_MANUAL_STOP:
                    {
                        over = true;
                        break;
                    }

                    // 音频设备插入
                    case (int)SDL.SDL_EventType.SDL_AUDIODEVICEADDED:
                    {
                        devicesChanged = true;
                        break;
                    }

                    // 音频设备移除
                    case (int)SDL.SDL_EventType.SDL_AUDIODEVICEREMOVED:
                    {
                        var dev = e.adevice.which;
                        if (dev == devId)
                        {
                            SetDevice(SDLGlobal.PLAYBACK_EMPTY_DEVICE);
                            over = true;
                        }
                        devicesChanged = true;
                        break;
                    }
                }
            }

            if (over)
            {
                break;
            }

            if (devicesChanged)
            {
                onDevicesUpdated?.Invoke();
            }

            SDL.SDL_Delay(SDLGlobal.PLAYBACK_POLL_INTERVAL);
        }

        // 设置暂停标识位
        SDL.SDL_PauseAudioDevice(devId, 1);

        // 解除固定缓冲区
        gch.Free();
    }
}