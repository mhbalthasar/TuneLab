using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using TuneLab.ExperimentalPatch.VstBridge;

// 一条已连上的管道会话：收消息、分发、回消息。
//
// 【它故意不做什么】不知道插件、不知道 VST、不知道宿主是谁。全部业务在 PluginHost 里。
// 这里只负责"一帧一帧地把事办了"，于是协议错了能在这层暴露出来，插件崩了不会带着协议一起崩。
internal sealed class BridgeSession : IDisposable
{
    readonly NamedPipeServerStream mPipe;
    readonly PluginHost mVst2 = new();
    readonly Vst3PluginHost mVst3 = new();

    // 槽位 → 宿主实现。Load 时按 format 决定，之后的每一条消息都照它转发。
    // 只在桥的主线程读写，不需要锁。
    readonly Dictionary<int, IBridgePluginHost> mOwner = [];

    readonly byte[] mLength = new byte[4];
    readonly SharedAudioRing? mInput;
    readonly SharedAudioRing? mOutput;
    bool mRunning = true;

    public BridgeSession(NamedPipeServerStream pipe, SharedAudioRing? input = null, SharedAudioRing? output = null)
    {
        mPipe = pipe;
        mInput = input;
        mOutput = output;

        if (input != null && output != null)
        {
            mVst2.Attach(input, output);
            mVst3.Attach(input, output);
        }
    }

    public void Run()
    {
        while (mRunning)
        {
            // 读一帧。读不动（EOF / 长度头坏了）= 宿主走了：直接收工。
            if (!TryReadFrame(out string? json, out byte[] payload))
                break;

            BridgeMessageKind kind = PeekKind(json);

            switch (kind)
            {
                case BridgeMessageKind.Hello:
                    HandleHello(json);
                    break;

                case BridgeMessageKind.Ping:
                    Write(JsonSerializer.Serialize(new PingAck()));
                    break;

                case BridgeMessageKind.LoadPlugin:
                    HandleLoad(json, payload);
                    break;

                case BridgeMessageKind.UnloadPlugin:
                    HandleUnload(json);
                    break;

                case BridgeMessageKind.ProcessRequest:
                    HandleProcess(json, payload);
                    break;

                case BridgeMessageKind.GetState:
                    HandleGetState(json);
                    break;

                case BridgeMessageKind.SetState:
                    HandleSetState(json);
                    break;

                case BridgeMessageKind.GetParameters:
                    HandleGetParameters(json);
                    break;

                case BridgeMessageKind.SetParameter:
                    HandleSetParameter(json);
                    break;

                case BridgeMessageKind.OpenEditor:
                    HandleOpenEditor(json);
                    break;

                case BridgeMessageKind.CloseEditor:
                    HandleCloseEditor(json);
                    break;

                case BridgeMessageKind.Shutdown:
                    Write(JsonSerializer.Serialize(new ShutdownAck()));
                    mRunning = false;
                    break;

                default:
                    Write(JsonSerializer.Serialize(new ErrorMessage { Message = "unsupported message " + kind }));
                    break;
            }
        }
    }

    // ── 各个处理器：解析 → 干活 → 回话。解析失败一律回 Error，绝不让会话死掉。 ──

    // [EP-MIXER-27] 握手要校验协议版本：版本决定**共享环的布局**（2 声道还是 4 声道）。
    // 版本不匹配时"能连上"比"连不上"更危险 —— 会按错的步长解读同一段内存。
    // 所以这里回 Ok=false 并结束会话，让宿主把进程杀掉、去用配套的新桥。
    void HandleHello(string? json)
    {
        HelloMessage? message = Deserialize<HelloMessage>(json);

        if (message == null || message.Version != BridgeProtocol.ProtocolVersion)
        {
            string reason = message == null
                ? "bad Hello message"
                : $"protocol version mismatch (host {message.Version}, bridge {BridgeProtocol.ProtocolVersion})";

            Write(JsonSerializer.Serialize(new HelloAckMessage { Ok = false, Reason = reason }));
            mRunning = false;
            return;
        }

        Write(JsonSerializer.Serialize(new HelloAckMessage
        {
            Ok = true,
            Formats = Vst3PluginHost.IsSupported
                ? [PluginFormats.Vst2, PluginFormats.Vst3]
                : [PluginFormats.Vst2],
        }));
    }

    void HandleLoad(string? json, byte[] payload)
    {
        LoadPluginMessage? message = Deserialize<LoadPluginMessage>(json);
        if (message == null)
        {
            WriteError("bad LoadPlugin message");
            return;
        }

        // 同一个槽位换插件（或换格式）：先把旧的从它自己的宿主里卸掉。
        if (mOwner.Remove(message.Slot, out var previous))
            previous.Unload(message.Slot);

        IBridgePluginHost host = string.Equals(message.Format, PluginFormats.Vst3, StringComparison.OrdinalIgnoreCase)
            ? mVst3
            : mVst2;

        // 附带音频段在这个消息里用不到（加载不需要音频），忽略 payload。
        var result = host.Load(message, out int inputChannels, out int outputChannels);

        if (result.Ok)
            mOwner[message.Slot] = host;

        Write(JsonSerializer.Serialize(new LoadPluginResultMessage
        {
            Slot = message.Slot,
            Ok = result.Ok,
            Reason = result.Reason,
            Name = result.Name,
            Vendor = result.Vendor,
            LatencySamples = result.LatencySamples,
            InputChannels = inputChannels,
            OutputChannels = outputChannels,
            IsSynth = result.IsSynth,
            HasEditor = result.HasEditor,
            NeedsOpenGl = result.NeedsOpenGl,
        }));
    }

    // 按槽位找当前承载它的宿主。找不到时用 VST2 的（对未知槽位统一回"失败"）。
    IBridgePluginHost OwnerOf(int slotNumber)
        => mOwner.TryGetValue(slotNumber, out var host) ? host : mVst2;

    void HandleOpenEditor(string? json)
    {
        OpenEditorMessage? message = Deserialize<OpenEditorMessage>(json);
        if (message == null)
        {
            WriteError("bad OpenEditor message");
            return;
        }

        var result = OwnerOf(message.Slot).OpenEditor(message.Slot, message.Title);

        Write(JsonSerializer.Serialize(new OpenEditorResultMessage
        {
            Slot = message.Slot,
            Ok = result.Ok,
            Reason = result.Reason,
            ChildWindows = result.ChildWindows,
            // VST3 回插件自己那扇顶层窗的句柄；VST2 的窗口归桥自己置前，一直是 0。
            WindowHandle = result.Window.ToInt64(),
        }));
    }

    void HandleCloseEditor(string? json)
    {
        CloseEditorMessage? message = Deserialize<CloseEditorMessage>(json);
        if (message == null)
        {
            WriteError("bad CloseEditor message");
            return;
        }

        bool ok = OwnerOf(message.Slot).CloseEditor(message.Slot);
        Write(JsonSerializer.Serialize(new CloseEditorResultMessage
        {
            Slot = message.Slot,
            Ok = ok,
        }));
    }

    void HandleUnload(string? json)
    {
        UnloadPluginMessage? message = Deserialize<UnloadPluginMessage>(json);
        if (message == null)
        {
            WriteError("bad UnloadPlugin message");
            return;
        }

        bool ok = OwnerOf(message.Slot).Unload(message.Slot);
        mOwner.Remove(message.Slot);
        Write(JsonSerializer.Serialize(new UnloadPluginResultMessage { Slot = message.Slot, Ok = ok }));
    }

    void HandleProcess(string? json, byte[] payload)
    {
        ProcessRequest? message = Deserialize<ProcessRequest>(json);
        if (message == null)
        {
            WriteError("bad ProcessRequest message");
            return;
        }

        // 音频数据走共享内存（见 BridgeSharedMemory 的说明）：
        // 这里 payload 只是"宿主要求处理一块"的指令，真正的样本在共享环里。
        bool processed = OwnerOf(message.Slot).Process(message.Slot, message.Bypass, out int frames, out int channels);

        Write(JsonSerializer.Serialize(new ProcessResult
        {
            Slot = message.Slot,
            Ok = true,
            Processed = processed,
            Frames = frames,
            Channels = channels,
        }));
    }

    void HandleGetState(string? json)
    {
        // 形态上走 GetState：协议里没有单独的消息类型（StateChunk 双向复用）。
        var message = Deserialize<GetStateMessage>(json);
        if (message == null)
        {
            WriteError("bad GetState message");
            return;
        }

        byte[]? state = OwnerOf(message.Slot).GetState(message.Slot);

        Write(JsonSerializer.Serialize(new StateChunkMessage
        {
            Slot = message.Slot,
            Base64 = state == null ? string.Empty : Convert.ToBase64String(state),
        }));
    }

    void HandleSetState(string? json)
    {
        var message = Deserialize<SetStateMessage>(json);
        if (message == null)
        {
            WriteError("bad SetState message");
            return;
        }

        bool ok = OwnerOf(message.Slot).SetState(message.Slot, message.Base64);

        Write(JsonSerializer.Serialize(new SetStateResultMessage { Slot = message.Slot, Ok = ok }));
    }

    void HandleGetParameters(string? json)
    {
        var message = Deserialize<GetParametersMessage>(json);
        if (message == null)
        {
            WriteError("bad GetParameters message");
            return;
        }

        var parameters = OwnerOf(message.Slot).GetParameters(message.Slot);

        Write(JsonSerializer.Serialize(new ParameterListMessage
        {
            Slot = message.Slot,
            Parameters = parameters,
        }));
    }

    void HandleSetParameter(string? json)
    {
        var message = Deserialize<SetParameterMessage>(json);
        if (message == null)
        {
            WriteError("bad SetParameter message");
            return;
        }

        OwnerOf(message.Slot).SetParameter(message.Slot, message.Id, message.Value);

        Write(JsonSerializer.Serialize(new SetParameterResultMessage { Slot = message.Slot, Ok = true }));
    }

    // ── 帧读写 ──

    static T? Deserialize<T>(string? json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json ?? "null");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static BridgeMessageKind PeekKind(string? json)
    {
        // 只取 kind 一个字段：不用把整条消息反序列化就知道往哪走。
        // 取不到就当 Error 处理（下面调 WriteError）。
        try
        {
            using var document = JsonDocument.Parse(json ?? "null");

            if (document.RootElement.TryGetProperty("kind", out var kind)
                && Enum.TryParse<BridgeMessageKind>(kind.ToString(), out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
        }

        return BridgeMessageKind.Error;
    }

    // payload 非 null 时作为同一帧的附带二进制段（音频）。
    void Write(string json, byte[]? payload = null)
    {
        byte[] head = Encoding.UTF8.GetBytes(json);
        byte[] length = BitConverter.GetBytes(head.Length + (payload?.Length ?? 0));

        mPipe.Write(length);
        mPipe.Write(head);

        if (payload != null && payload.Length > 0)
            mPipe.Write(payload);

        mPipe.Flush();
    }

    void WriteError(string message)
        => Write(JsonSerializer.Serialize(new ErrorMessage { Message = message }));

    bool TryReadFrame(out string? json, out byte[] payload)
    {
        json = null;
        payload = [];

        try
        {
            if (!ReadExact(mLength, 4))
                return false;

            int total = BitConverter.ToInt32(mLength);
            if (total <= 0 || total > BridgeProtocol.MaxFrameBytes)
                return false;

            var frame = new byte[total];
            if (!ReadExact(frame, total))
                return false;

            // JSON 头到 payload 的分界：宿主发来的是 [JSON][二进制]。
            // 用"JSON 自描述长度"切不开，所以约定：先读 JSON（遇到能解析的位置就停）。
            // 简化：宿主在控制消息里不带 payload，只有一个例外（ProcessRequest 的音频输入）。
            // 那个例外由 HandleProcess 用 message.Frames * Channels 反推长度，
            // 因此这里只需要把整帧交给它。
            payload = frame;

            // 尝试把整帧当 JSON 解析；解析成功就说明没有附带段（绝大多数消息）。
            string text = Encoding.UTF8.GetString(frame);

            try
            {
                using var document = JsonDocument.Parse(text);
                json = text;
                return true;
            }
            catch (JsonException)
            {
                // 带附带的帧：JSON 头后面粘了二进制。宿主约定 [JSON][0x00][payload]。
                int zero = Array.IndexOf(frame, (byte)0);
                if (zero <= 0)
                    return false;

                json = Encoding.UTF8.GetString(frame, 0, zero);

                payload = new byte[frame.Length - zero - 1];
                Array.Copy(frame, zero + 1, payload, 0, payload.Length);
                return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    bool ReadExact(byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = mPipe.Read(buffer, read, count - read);
            if (n <= 0)
                return false;

            read += n;
        }

        return true;
    }

    public void Dispose()
    {
        try
        {
            mPipe.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
