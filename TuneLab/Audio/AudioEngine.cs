using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MeltySynth;
using TuneLab.Audio.SDL2;
using TuneLab.ExperimentalPatch.MixerEngine;
using TuneLab.Foundation;
using TuneLab.Utils;

namespace TuneLab.Audio;

internal static class AudioEngine
{
    public static event Action? PlayStateChanged;
    public static event Action? ProgressChanged;
    public static bool IsPlaying => mAudioSampleProvider.IsPlaying;
    public static INotifiableProperty<int> SampleRate { get; } = new NotifiableProperty<int>(44100);
    // 播放头显示的位置 = 用户**听到**的位置。它来自 SDL 回调里"写下一块之前已经
    // 写入设备流的帧数"，而不是预读头；预读头领先设备里还没播完的那一块。
    public static double CurrentTime => mAudioSampleProvider.CurrentTime;
    // 合成调度用的位置 = 预读头。它必须领先于听到的位置，否则会追着已缓冲的音频
    // 才开始合成，容易在块边界上供不上。两者差一个（或两个）设备块。
    public static double SchedulingTime => mAudioSampleProvider.SchedulingTime;
    public static double MasterGain { get; set; } = 0;

    // [EP-MIXER-24] 声向法则（roadmap 2.5）。默认线性 = 上游老公式
    // （left = v(1-p)、right = v(1+p)，硬偏时那一侧 +6 dB）。
    // 等功率法则（−3 / −4.5 dB）是给用户的选项：pan 移动时音量听起来不变。
    // 切它会立刻改变所有通道的声音，所以必须能立刻推到混音器上（见 setter）。
    public static PanLaw PanLaw
    {
        get => AudioGraph.PanLaw;
        set => AudioGraph.PanLaw = value;
    }
    public static double EndTime => AudioGraph.EndTime;
    public static INotifiableProperty<int> BufferSize { get; } = new NotifiableProperty<int>(1024);

    // [EP-MIXER-30] 效果器插件缓冲时间（毫秒）→ 设备缓冲采样数。设备缓冲是"一次交给混音图的帧数"，
    // 也是桥进程一轮 process 的音频时长；毫秒口径与采样率无关，用户不必知道 44.1k/48k 的差别。
    // 下限 64 帧（低于它的设备缓冲在部分驱动上不可用），上限 65535 帧
    // （SDL 的 spec.samples 是 ushort；200 ms @ 192 kHz = 38400 也装得下）。
    public static int SamplesFromMilliseconds(int milliseconds, int sampleRate)
    {
        if (sampleRate <= 0)
            sampleRate = 44100;

        long samples = (long)Math.Round(Math.Clamp(milliseconds, 1, 1000) * (double)sampleRate / 1000.0);
        return (int)Math.Clamp(samples, 64, 65535);
    }

    public static int MillisecondsFromSamples(int samples, int sampleRate)
    {
        if (sampleRate <= 0)
            sampleRate = 44100;

        return (int)Math.Round(samples * 1000.0 / sampleRate);
    }

    public static INotifiableProperty<string> CurrentDriver { get; } = new NotifiableProperty<string>(string.Empty);
    public static INotifiableProperty<string> CurrentDevice { get; } = new NotifiableProperty<string>(string.Empty);
    // 设置窗可能在音频引擎初始化之前被某个扩展打开；null 时返回空/自动，避免动态选项抛 NRE。
    public static IReadOnlyList<string> GetAllDrivers() => mAudioPlaybackHandler?.GetAllDrivers() ?? Array.Empty<string>();
    public static IReadOnlyList<string> GetAllDevices() => mAudioPlaybackHandler?.GetAllDevices() ?? new[] { IAudioPlaybackHandler.AutoDeviceName };

    public static void Init()
    {
        // [EP-MIXER-31] 音频优先：进程整体抬到 High（界面线程也随之变快，但真正压过它的是下面
        // SDL 回调线程的 Highest —— 见 SDLPlaybackHandler.onBufferEnd）。
        // 失败不致命（受限账户 / 系统策略），只记一条警告。
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.High;
        }
        catch (Exception ex)
        {
            Log.Warning("failed to raise process priority: " + ex.Message);
        }

        mAudioSampleProvider.SampleRate = SampleRate.Value;
        mAudioPlaybackHandler = new SDLPlaybackHandler() { SampleRate = SampleRate.Value, BufferSize = BufferSize.Value, ChannelCount = 2 };
        mAudioPlaybackHandler.Init(mAudioSampleProvider);
        SetDriverToPlaybackHandler();
        SetDeviceToPlaybackHandler();
        // 【必须是具名方法，好在 Destroy 里退订】这三个事件都由 SDL 侧 marshal 回 UI 线程（context.Post）。
        // Destroy 里 handler.Destroy() 会把设备切成"空设备"——那本身就是一次设备变更，于是又 Post 一个
        // 回调进 UI 队列，而紧接着的下一行就把 mAudioPlaybackHandler 置空了。等 Dispatcher 轮到那个回调时，
        // 回调里访问已为 null 的 handler → NullReferenceException，而它是在 Dispatcher 操作里抛的、无人
        // 接管 → 退出期直接崩掉进程（用户看到的是关窗口时闪退，未保存的工作一并没了；因为要赶在进程真正
        // 结束前被取到才会炸，所以表现成偶发）。原先这三个订阅是匿名 lambda、压根没法退订。
        mAudioPlaybackHandler.ProgressChanged += OnHandlerProgressChanged;
        mAudioPlaybackHandler.CurrentDeviceChanged += OnHandlerCurrentDeviceChanged;
        mAudioPlaybackHandler.DevicesChanged += OnHandlerDevicesChanged;

        InitDefaultSoundFont();

        ProgressChanged += OnProgressChanged;
        SampleRate.Modified.Subscribe(OnSampleRateModified);
        BufferSize.Modified.Subscribe(OnBufferSizeModified);
        CurrentDriver.Modified.Subscribe(OnCurrentDriverModified);
        CurrentDevice.Modified.Subscribe(OnCurrentDeviceModified);

        mAudioPlaybackHandler.Start();
    }

    public static void Destroy()
    {
        if (mAudioPlaybackHandler == null)
        {
            Log.Error("Engine is not inited!");
            return;
        }

        mAudioPlaybackHandler.Stop();

        // 【先退订，再销毁】顺序是判据本身：handler.Destroy() 切空设备会再 Post 一个设备变更回调，
        // 而它执行时 handler 已被置空（见 Init 里那段注释）。退订之后那个回调 Invoke 时调用列表是空的，
        // 故已经排在队列里的操作也无害——两者都在 UI 线程串行，不存在"退订与执行并发"。
        mAudioPlaybackHandler.ProgressChanged -= OnHandlerProgressChanged;
        mAudioPlaybackHandler.CurrentDeviceChanged -= OnHandlerCurrentDeviceChanged;
        mAudioPlaybackHandler.DevicesChanged -= OnHandlerDevicesChanged;

        ProgressChanged -= OnProgressChanged;
        SampleRate.Modified.Unsubscribe(OnSampleRateModified);
        BufferSize.Modified.Unsubscribe(OnBufferSizeModified);   // Init 订阅了四个，这一个原先漏在这里
        CurrentDriver.Modified.Unsubscribe(OnCurrentDriverModified);
        CurrentDevice.Modified.Unsubscribe(OnCurrentDeviceModified);

        mAudioPlaybackHandler.Destroy();
        mAudioPlaybackHandler = null;
    }

    // SDL 侧三个事件的落点（具名 = 可退订，理由见 Init 与 Destroy 里的注释）。
    static void OnHandlerProgressChanged()
    {
        if (IsPlaying)
            ProgressChanged?.Invoke();
    }

    static void OnHandlerCurrentDeviceChanged()
    {
        CurrentDevice.Value = mAudioPlaybackHandler!.CurrentDevice;
        // 新设备的流从 0 重新计数：播放头重定位到当前听到的位置。
        mAudioSampleProvider.ResetAfterDeviceChange();
        mAudioPlaybackHandler.Start();
    }

    static void OnHandlerDevicesChanged()
    {
        SetDeviceToPlaybackHandler();
    }

    public static void Play()
    {
        mAudioSampleProvider.IsPlaying = true;
        // IsPlaying 的 setter 已把预读头拉回"听到的位置"；丢掉旧块，让设备从那里重新开始。
        mAudioPlaybackHandler?.Flush();
        PlayStateChanged?.Invoke();
    }

    public static void Pause()
    {
        mAudioSampleProvider.IsPlaying = false;
        // 暂停要立刻停声：旧块里可能还有最多一个缓冲的音频。
        mAudioPlaybackHandler?.Flush();
        PlayStateChanged?.Invoke();
    }

    public static void Seek(double time)
    {
        mAudioSampleProvider.Seek(time);
        mAudioPlaybackHandler?.Flush();
        ProgressChanged?.Invoke();
    }

    public static void AddTrack(IAudioTrack track)
    {
        AudioGraph.AddTrack(track);
    }

    public static void RemoveTrack(IAudioTrack track)
    {
        AudioGraph.RemoveTrack(track);
    }

    // [EP-MIXER-5] AUX 总线的增删（与轨同一套：结构变化 → 重建混音图快照）。
    public static void AddAux(TuneLab.Data.AuxBus aux)
    {
        AudioGraph.AddAux(aux);
    }

    public static void RemoveAux(TuneLab.Data.AuxBus aux)
    {
        AudioGraph.RemoveAux(aux);
    }

    // [EP-MIXER-5] 路由（output / send）变了：边变了，重建快照。
    // 推子 / 声像 / 静音是活值，不走这里（每块现读，不需要重建）。
    public static void RebuildMixerGraph()
    {
        AudioGraph.RebuildMixerGraph();
    }

    // [EP-MIXER-29] 批量结构变更的作用域：用 using 把一段"会改很多通道 output / send / 插入"
    // 的操作包起来，期间的重建全部攒着，离开作用域时统一编一次图。
    // 装载工程、套用路由载荷、脚本批量改路由都走这里；不在 UI 线程之外调用。
    public static IDisposable SuspendMixerGraphRebuild()
    {
        return AudioGraph.SuspendRebuild();
    }

    // [EP-MIXER-19] Master 的插入链由工程提供（Project.MasterInserts）：换工程时跟着换。
    public static void SetMasterInserts(DataObjectList<TuneLab.Data.InsertSlot>? inserts)
    {
        AudioGraph.SetMasterInserts(inserts);
    }

    // [EP-MIXER-28] Master 的 Ø 由工程提供（Project.MasterPhaseInvert）。
    // 与插入链一样跟随当前工程；值本身是活值，音频线程每块现读。
    public static void SetMasterPhaseInvert(DataProperty<bool>? phaseInvert)
    {
        AudioGraph.SetMasterPhaseInvert(phaseInvert);
    }

    // [EP-MIXER-AUTO] 插入参数自动化的时间换算器（工程装载 / tempo / 采样率变化时由宿主换入）。
    public static void SetTimeMapper(IMixTimeMapper? mapper)
    {
        AudioGraph.SetTimeMapper(mapper);
    }

    public static void ExportTrack(string filePath, IAudioTrack track, bool isStereo)
    {
        ExportTrack(filePath, track, isStereo, SampleRate.Value, AudioEncodeSettings.Wav(16));
    }

    // [EP-MIXER-33] includeEffects = 分轨导出经过该轨自己的插入链（Master 导出始终走完整图）。
    public static void ExportTrack(string filePath, IAudioTrack track, bool isStereo, int outputSampleRate, AudioEncodeSettings settings, IProgress<double>? progress = null, CancellationToken cancellationToken = default, double startTime = 0, double? endTime = null, bool includeEffects = false)
    {
        double defaultEndTime = Math.Max(track.EndTime, 0) + 1;
        int startPosition = (Math.Max(startTime, 0) * SampleRate.Value).Floor();
        int endPosition = ((endTime ?? defaultEndTime) * SampleRate.Value).Ceil();
        endPosition = Math.Max(endPosition, startPosition);
        int length = endPosition - startPosition;
        int channelCount = isStereo ? 2 : 1;
        float[] buffer = new float[length * channelCount];

        // 带效果时走"单轨最小混音图"：音源 → 推子/声像 → 插入链 → 设备出口。
        // 不带效果保持原来的 AddData 路径，逐样本结果不变（老工程的导出语义不变）。
        using var effectsRenderer = includeEffects ? new TrackExportRenderer(AudioGraph, track) : null;

        // Process in chunks for progress reporting
        int chunkSize = BufferSize.Value;
        for (int chunkStart = startPosition; chunkStart < endPosition; chunkStart += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunkEnd = Math.Min(chunkStart + chunkSize, endPosition);

            if (effectsRenderer != null)
                effectsRenderer.Render(chunkStart, chunkEnd, isStereo, buffer, (chunkStart - startPosition) * channelCount);
            else
                AudioGraph.AddData(track, chunkStart, chunkEnd, isStereo, buffer, (chunkStart - startPosition) * channelCount);

            progress?.Report(length > 0 ? (double)(chunkEnd - startPosition) / length * 0.8 : 0.8);
        }

        progress?.Report(0.8);
        cancellationToken.ThrowIfCancellationRequested();

        if (outputSampleRate != SampleRate.Value)
            buffer = AudioUtils.Resample(buffer, channelCount, SampleRate.Value, outputSampleRate);

        progress?.Report(0.9);
        cancellationToken.ThrowIfCancellationRequested();

        AudioUtils.Encode(filePath, buffer, outputSampleRate, channelCount, settings);
        progress?.Report(1.0);
    }

    public static void ExportMaster(string filePath, bool isStereo)
    {
        ExportMaster(filePath, isStereo, SampleRate.Value, AudioEncodeSettings.Wav(16));
    }

    public static void ExportMaster(string filePath, bool isStereo, int outputSampleRate, AudioEncodeSettings settings, IProgress<double>? progress = null, CancellationToken cancellationToken = default, double startTime = 0, double? endTime = null)
    {
        int startPosition = (Math.Max(startTime, 0) * SampleRate.Value).Floor();
        int endPosition = ((endTime ?? AudioGraph.EndTime) * SampleRate.Value).Ceil();
        endPosition = Math.Max(endPosition, startPosition);
        int length = endPosition - startPosition;
        int channelCount = isStereo ? 2 : 1;
        float[] buffer = new float[length * channelCount];

        // Process in chunks for progress reporting
        int chunkSize = BufferSize.Value;
        for (int chunkStart = startPosition; chunkStart < endPosition; chunkStart += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunkEnd = Math.Min(chunkStart + chunkSize, endPosition);
            AudioGraph.MixData(chunkStart, chunkEnd, isStereo, buffer, (chunkStart - startPosition) * channelCount);
            progress?.Report(length > 0 ? (double)(chunkEnd - startPosition) / length * 0.8 : 0.8);
        }

        progress?.Report(0.8);
        cancellationToken.ThrowIfCancellationRequested();

        if (outputSampleRate != SampleRate.Value)
            buffer = AudioUtils.Resample(buffer, channelCount, SampleRate.Value, outputSampleRate);

        progress?.Report(0.9);
        cancellationToken.ThrowIfCancellationRequested();

        AudioUtils.Encode(filePath, buffer, outputSampleRate, channelCount, settings);
        progress?.Report(1.0);
    }

    public static void InvokeRealtimeAmplitude(IAudioTrack track,out Tuple<double,double>? amplitude)
    {
        amplitude = null;

        if (track.IsMute) return;
        bool hasSolo = AudioGraph.Tracks.Where(t => t.IsSolo).Any();
        if (hasSolo && !track.IsSolo) return;

        double Sample2Amplitude(float Sample)
        {
            return Math.Abs(Sample);
        }
        double Amplitude2Db(double amplitude)
        {
            double referenceAmplitude = 1.0;
            double amplitudeRatio = amplitude / referenceAmplitude;
            double db = 20 * Math.Log10(amplitudeRatio);
            return db;
        }

        float[] amp = [0, 0];
        {
            int sampleWindow = 64;
            float[] buffer = new float[sampleWindow * 2];
            int position = (CurrentTime * SampleRate.Value).Ceil();
            AudioGraph.AddData(track, position, position + sampleWindow, true, buffer, 0);
            for (int i = 0; i < sampleWindow * 2; i += 2) { amp[0] = (float)Math.Max(amp[0], Sample2Amplitude(buffer[i])); amp[1] = (float)Math.Max(amp[1], Sample2Amplitude(buffer[i + 1])); };
        }
        amplitude = new Tuple<double, double>(
                Amplitude2Db(amp[0]), //L
                Amplitude2Db(amp[1])  //R
                );
    }

    // [EP-MIXER-3] AUX / Master 的电平（按混音图节点取峰值，推子之后 —— DESIGN.md §3.4）。
    // 只在播放时有效：停止时表归零（§4.5），所以这里不返回"最后一帧"。
    public static void InvokeRealtimeChannelAmplitude(string channelId, out Tuple<double, double>? amplitude)
    {
        amplitude = null;

        if (!IsPlaying)
            return;

        if (!AudioGraph.TryGetChannelPeak(channelId, out double left, out double right))
            return;

        amplitude = new Tuple<double, double>(left, right);
    }

    // [EP-MIXER-22] 响度读数（LUFS / True Peak）。与峰值同一套规矩：
    // 只有播放中才有意义 —— 停着的时候表该回零，不该停在最后的读数上。
    public static void InvokeRealtimeChannelLoudness(string channelId, out double lufs, out double truePeakDb)
    {
        lufs = double.NegativeInfinity;
        truePeakDb = double.NegativeInfinity;

        if (!IsPlaying)
            return;

        AudioGraph.TryGetChannelLufs(channelId, out lufs);
        AudioGraph.TryGetChannelTruePeakDb(channelId, out truePeakDb);
    }

    // [EP-MIXER-3] 轨道的电平，取自混音图的轨道节点（推子与声像**之后**，与 AUX / Master 同口径）。
    //
    // 为什么不用上面的 InvokeRealtimeAmplitude：那个读的是音源本身（pre-fader / pre-pan），
    // 于是调音台上推子推到底表不动、声像拧到一边两根柱子还是一样高——表就成了摆设。
    // 轨道节点 id 是编译时的序号（track-<i>），所以按图的轨道顺序找下标。
    public static void InvokeRealtimeTrackLoudness(IAudioTrack track, out double lufs, out double truePeakDb)
    {
        lufs = double.NegativeInfinity;
        truePeakDb = double.NegativeInfinity;

        if (!IsPlaying)
            return;

        // 与 InvokeRealtimeTrackAmplitude 同一套下标算法：第 i 条轨 = 图里的 "track-i"。
        int index = 0;
        foreach (var candidate in AudioGraph.Tracks)
        {
            if (ReferenceEquals(candidate, track))
            {
                AudioGraph.TryGetChannelLufs("track-" + index, out lufs);
                AudioGraph.TryGetChannelTruePeakDb("track-" + index, out truePeakDb);
                return;
            }

            index++;
        }
    }

    public static void InvokeRealtimeTrackAmplitude(IAudioTrack track, out Tuple<double, double>? amplitude)
    {
        amplitude = null;

        if (!IsPlaying)
            return;

        int index = 0;
        foreach (var candidate in AudioGraph.Tracks)
        {
            if (ReferenceEquals(candidate, track))
            {
                if (AudioGraph.TryGetChannelPeak("track-" + index, out double left, out double right))
                    amplitude = new Tuple<double, double>(left, right);

                return;
            }

            index++;
        }
    }

    public static void LoadKeySamples(string path)
    {
        mKeySamplesPath = path;
        try
        {
            mKeySamples.Fill(null);

            // 路径指向 .sf2 文件：作为用户预览音源（经 MeltySynth），优先于内置。
            if (File.Exists(path) && Path.GetExtension(path).Equals(".sf2", StringComparison.OrdinalIgnoreCase))
            {
                SetUserSoundFont(path);
                return;
            }

            // 非 SF2：清除用户音源、回落到内置；目录则按旧机制逐键 WAV。
            SetUserSoundFont(null);
            if (!Directory.Exists(path))
                return;

            foreach (var file in Directory.GetFiles(path))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (int.TryParse(name, out var keyNumber))
                {
                    int keyIndex = keyNumber - MusicTheory.MIN_PITCH;
                    if ((uint)keyIndex < MusicTheory.PITCH_COUNT)
                    {
                        try
                        {
                            int sampleRate = SampleRate.Value;
                            var data = AudioUtils.Decode(file, ref sampleRate);
                            switch (data.Length)
                            {
                                case 1:
                                    mKeySamples[keyIndex] = new MonoAudioData(data[0]);
                                    break;
                                case 2:
                                    mKeySamples[keyIndex] = new StereoAudioData(data[0], data[1]);
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Failed to decode key sample: " + file + " " + ex);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load key samples: " + ex);
        }
    }

    public static void PlayKeySample(int keyNumber)
    {
        var keyIndex = keyNumber - MusicTheory.MIN_PITCH;
        if ((uint)keyIndex >= mKeySamples.Length)
            return;

        // 优先级：用户逐键 WAV > 预览音源（用户 SF2 或内置 SF2）渲染音。
        var keySample = mKeySamples[keyIndex] ?? GetPreviewKeySample(keyIndex);
        if (keySample == null)
            return;

        AudioPlayer.Play(keySample);
    }

    // 活动预览音源：用户 SF2 优先，否则内置 SF2。
    static SoundFont? ActiveSoundFont => mUserSoundFont ?? mDefaultSoundFont;

    // 加载内置默认预览音源（Upright Piano KW，CC0，随程序分发）。
    static void InitDefaultSoundFont()
    {
        try
        {
            var path = Path.Combine(PathManager.ResourcesFolder, "SoundFonts", DefaultSoundFontFileName);
            if (!File.Exists(path))
                Log.Warning("Default piano soundfont not found: " + path);
            else
                mDefaultSoundFont = new SoundFont(path);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load default piano soundfont: " + ex);
            mDefaultSoundFont = null;
        }
        RebuildPreviewSynthesizer();
    }

    // 用户自定义预览音源（按键采样路径指向的 .sf2 文件）；传 null 清除、回落到内置。
    static void SetUserSoundFont(string? path)
    {
        try
        {
            mUserSoundFont = path != null ? new SoundFont(path) : null;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load user soundfont: " + path + " " + ex);
            mUserSoundFont = null;
        }
        RebuildPreviewSynthesizer();
    }

    // 按活动音源 + 当前采样率重建 Synthesizer，并清空按键渲染缓存（音源或采样率变更后旧渲染音已失效）。
    static void RebuildPreviewSynthesizer()
    {
        lock (mPreviewLock)
        {
            Array.Clear(mPreviewKeySamples);
            var font = ActiveSoundFont;
            mPreviewSynthesizer = font != null ? new Synthesizer(font, SampleRate.Value) : null;
        }
    }

    // 各键首次触发时离线渲染一小段并缓存（懒加载，避开启动期渲染 128 键的开销）。
    static IAudioData? GetPreviewKeySample(int keyIndex)
    {
        lock (mPreviewLock)
        {
            if (mPreviewKeySamples[keyIndex] is { } cached)
                return cached;
            if (mPreviewSynthesizer == null)
                return null;

            int key = keyIndex + MusicTheory.MIN_PITCH;
            int sampleRate = SampleRate.Value;
            int sustainCount = (int)(sampleRate * PreviewSustainSeconds);
            int releaseCount = (int)(sampleRate * PreviewReleaseSeconds);

            var left = new float[sustainCount + releaseCount];
            var right = new float[sustainCount + releaseCount];

            // 每键独立渲染：清残留 → NoteOn 渲染延音段 → NoteOff 渲染释音尾。
            mPreviewSynthesizer.Reset();
            mPreviewSynthesizer.NoteOn(0, key, PreviewVelocity);
            mPreviewSynthesizer.Render(left.AsSpan(0, sustainCount), right.AsSpan(0, sustainCount));
            mPreviewSynthesizer.NoteOff(0, key);
            mPreviewSynthesizer.Render(left.AsSpan(sustainCount, releaseCount), right.AsSpan(sustainCount, releaseCount));

            var data = new StereoAudioData(left, right);
            mPreviewKeySamples[keyIndex] = data;
            return data;
        }
    }

    static void OnProgressChanged()
    {
        if (CurrentTime > AudioGraph.EndTime)
            Pause();
    }

    static void OnSampleRateModified()
    {
        mAudioSampleProvider.SampleRate = SampleRate.Value;
        if (mAudioPlaybackHandler != null)
            mAudioPlaybackHandler.SampleRate = SampleRate.Value;
        if (mKeySamplesPath != null)
            LoadKeySamples(mKeySamplesPath);
        RebuildPreviewSynthesizer();
    }

    static void OnBufferSizeModified()
    {
        if (mAudioPlaybackHandler != null)
        {
            mAudioPlaybackHandler.BufferSize = BufferSize.Value;
            // 设备被重新打开：流计数从 0 开始，播放头重定位到当前听到的位置。
            mAudioSampleProvider.ResetAfterDeviceChange();
            mAudioPlaybackHandler.Flush();
        }
    }

    static void OnCurrentDriverModified()
    {
        SetDriverToPlaybackHandler(); 
        SetDeviceToPlaybackHandler();
        if (mAudioPlaybackHandler != null)
        {
            mAudioSampleProvider.ResetAfterDeviceChange();
            mAudioPlaybackHandler.Flush();
        }
        mAudioPlaybackHandler?.Start();
    }

    static void OnCurrentDeviceModified()
    {
        SetDeviceToPlaybackHandler();
        if (mAudioPlaybackHandler != null)
        {
            mAudioSampleProvider.ResetAfterDeviceChange();
            mAudioPlaybackHandler.Flush();
        }
        mAudioPlaybackHandler?.Start();
    }

    static void SetDriverToPlaybackHandler()
    {
        // 关闭时 handler 可能已被释放，而设备/驱动变更的队列回调仍可能触发 —— 加守卫避免 shutdown 竞态 NRE。
        if (mAudioPlaybackHandler == null)
            return;

        var drivers = mAudioPlaybackHandler.GetAllDrivers();
        if (drivers.IsEmpty())
            return;

        var driver = drivers.Contains(CurrentDriver.Value) ? CurrentDriver.Value : drivers[0];
        if (mAudioPlaybackHandler.CurrentDriver != driver)
            mAudioPlaybackHandler.CurrentDriver = driver;

        CurrentDriver.Value = mAudioPlaybackHandler.CurrentDriver;
    }

    static void SetDeviceToPlaybackHandler()
    {
        if (mAudioPlaybackHandler == null)
            return;

        var devices = mAudioPlaybackHandler.GetAllDevices();
        if (devices.IsEmpty())
            return;

        var device = devices.Contains(CurrentDevice.Value) ? CurrentDevice.Value : devices[0];
        try
        {
            if (mAudioPlaybackHandler.CurrentDevice != device)
                mAudioPlaybackHandler.CurrentDevice = device;
        }
        catch (Exception ex)
        {
            // 选中的设备打不开（WASAPI 的块长/独占模式等）时不能让设置窗口或启动崩掉：
            // 记日志、回退到自动设备；自动设备也失败就保持无设备（静音），用户还能再选。
            Log.Error($"failed to set audio device '{device}': {ex.Message}");
            try
            {
                if (mAudioPlaybackHandler.CurrentDevice != devices[0])
                    mAudioPlaybackHandler.CurrentDevice = devices[0];
            }
            catch (Exception fallbackEx)
            {
                Log.Error($"failed to fall back to audio device '{devices[0]}': {fallbackEx.Message}");
            }
        }

        CurrentDevice.Value = mAudioPlaybackHandler.CurrentDevice;
    }

    // 分轨导出的"单轨最小混音图"渲染器。
    //
    // 【为什么不用整张图】整图会把这条轨的 output 路由、send、Master 推子/插入/限幅
    // 一起带上；分轨要的是"这条通道条自己的输出"。CompileSingleTrack 只编
    // 音源 → 通道（推子/声像/插入）→ device。
    //
    // 【插件实例】与播放共用 InsertInstanceFactory；调用方（Editor）在导出前停播，
    // 所以这里不会和音频线程同时喂同一个插件。
    sealed class TrackExportRenderer : IDisposable
    {
        readonly Mixer mMixer;
        readonly float[] mScratch = new float[Mixer.DefaultMaxBlockSize * 2];

        public TrackExportRenderer(AudioGraph graph, IAudioTrack track)
        {
            mMixer = new Mixer { LimiterEnabled = false, SampleRate = graph.SampleRate, PanLaw = graph.PanLaw };

            var compiler = new MixerGraphCompiler();
            mMixer.SetSnapshot(compiler.CompileSingleTrack(graph, track));
        }

        public void Render(int position, int endPosition, bool isStereo, float[] buffer, int offset)
        {
            int channelCount = isStereo ? 2 : 1;

            for (int chunkStart = position; chunkStart < endPosition;)
            {
                int n = Math.Min(endPosition - chunkStart, Mixer.DefaultMaxBlockSize);
                Array.Clear(mScratch, 0, n * 2);
                mMixer.Render(chunkStart, n, mScratch, 0);

                int write = offset + (chunkStart - position) * channelCount;

                if (isStereo)
                {
                    Array.Copy(mScratch, 0, buffer, write, n * 2);
                }
                else
                {
                    // 与旧路径的单声道下混一致：(L + R) / 2。
                    for (int i = 0; i < n; i++)
                        buffer[write + i] = (mScratch[i * 2] + mScratch[i * 2 + 1]) * 0.5f;
                }

                chunkStart += n;
            }
        }

        public void Dispose() { }
    }

    class AudioSampleProvider() : IAudioSampleProvider
    {
        public AudioGraph AudioGraph => mAudioGraph;
        public AudioPlayer AudioPlayer => mAudioPlayer;
        public int SampleRate { get => mAudioGraph.SampleRate; set => mAudioGraph.SampleRate = value; }

        public bool IsPlaying
        {
            get => mIsPlaying;
            set
            {
                if (mIsPlaying == value)
                    return;
                mIsPlaying = value;

                if (value)
                {
                    // 恢复播放时从"听到的位置"继续，而不是从预读头继续：预读头领先
                    // 已缓冲但还没播的音频，直接用它会让恢复后的声音跳过一段。
                    // 旧块由 AudioEngine.Play 负责 Flush。
                    lock (mSeekLockObject)
                    {
                        mGraphPosition = (int)mPlayedFrames;
                        mPlaybackStartPosition = (int)mPlayedFrames;
                        mRebase = true;
                    }
                }
            }
        }

        // [EP-MIXER-31] 用户听到的位置：由 SDL 回调累计的"已写入设备流"帧数映射回工程时间轴。
        public double CurrentTime => (double)mPlayedFrames / SampleRate;

        // 预读头：合成调度、效果图排序用。它领先 CurrentTime 一个（或两个）设备块。
        public double SchedulingTime => (double)mGraphPosition / SampleRate;

        // 播放头对齐：SDL 回调在写下一块之前给出 playedFrames（已经播完的绝对帧数）。
        // 跳转/恢复后第一次回调把 handler 的绝对帧数重定位到工程时间轴。
        public void OnSamplesConsumed(long playedFrames)
        {
            // 暂停/停止时设备可能还在消耗已缓冲的块；播放头应停在听到的位置，不跟着走。
            if (!mIsPlaying)
                return;

            lock (mSeekLockObject)
            {
                if (mRebase || playedFrames < mHandlerBase)
                {
                    mHandlerBase = playedFrames;
                    mPlayedBase = mPlaybackStartPosition;
                    mRebase = false;
                }

                mPlayedFrames = mPlayedBase + (playedFrames - mHandlerBase);
                if (mPlayedFrames < mPlaybackStartPosition)
                    mPlayedFrames = mPlaybackStartPosition;
            }
        }

        // 设备/驱动/块长变了：设备流从 0 重新计数，播放头要从"当前听到的位置"重定位。
        public void ResetAfterDeviceChange()
        {
            lock (mSeekLockObject)
            {
                mGraphPosition = (int)mPlayedFrames;
                mPlaybackStartPosition = (int)mPlayedFrames;
                mRebase = true;
            }
        }

        public void Read(float[] buffer, int offset, int count)
        {
            // [EP-MIXER-3] Master 增益与预览音都交给混音图：图的 Master 节点负责乘增益，
            // 预览音作为 Master 的额外输入在增益**之前**混入（与旧路径 MixData → 预览 → MasterGain 同序）。
            if (IsPlaying)
            {
                lock (mSeekLockObject)
                {
                    int position = mGraphPosition;
                    int endPosition = position + count;
                    try
                    {
                        mAudioGraph.MixData(position, endPosition, true, buffer, offset, PreviewSource);
                    }
                    catch { }
                    mGraphPosition = endPosition;
                }
            }
            else
            {
                mAudioGraph.MixIdle(count, buffer, offset, PreviewSource);
            }
        }

        public void Seek(double time)
        {
            lock (mSeekLockObject)
            {
                mGraphPosition = (int)(time * SampleRate);
                mPlaybackStartPosition = mGraphPosition;
                mPlayedFrames = mGraphPosition;
                mRebase = true;
            }
        }

        readonly AudioPlayer mAudioPlayer = new();
        readonly AudioGraph mAudioGraph = new();
        int mGraphPosition = 0;
        int mPlaybackStartPosition = 0;
        long mPlayedFrames = 0;
        long mPlayedBase = 0;
        long mHandlerBase = 0;
        bool mRebase = true;
        bool mIsPlaying = false;
        readonly object mSeekLockObject = new();

        // 预览音作为"直接混进 Master 输入"的音源。懒建（一次），之后每块复用同一个对象。
        PreviewMixSource? mPreviewSource;
        PreviewMixSource PreviewSource => mPreviewSource ??= new PreviewMixSource(mAudioPlayer);
    }

    static IAudioPlaybackHandler? mAudioPlaybackHandler;
    static AudioSampleProvider mAudioSampleProvider = new();
    static AudioGraph AudioGraph => mAudioSampleProvider.AudioGraph;
    static AudioPlayer AudioPlayer => mAudioSampleProvider.AudioPlayer;

    static readonly IAudioData?[] mKeySamples = new IAudioData?[MusicTheory.PITCH_COUNT];
    static string? mKeySamplesPath = null;

    // 预览音源（经 MeltySynth 渲染）：用户 SF2 优先，否则内置 SF2（Resources/SoundFonts 下随程序分发，CC0 免任何义务）。
    const string DefaultSoundFontFileName = "UprightPianoKW.sf2";
    const int PreviewVelocity = 100;
    const double PreviewSustainSeconds = 0.5;
    const double PreviewReleaseSeconds = 0.8;
    static SoundFont? mDefaultSoundFont = null;
    static SoundFont? mUserSoundFont = null;
    static Synthesizer? mPreviewSynthesizer = null;   // 由 ActiveSoundFont 构建
    static readonly IAudioData?[] mPreviewKeySamples = new IAudioData?[MusicTheory.PITCH_COUNT];
    static readonly object mPreviewLock = new();
}


