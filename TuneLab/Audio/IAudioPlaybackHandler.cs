using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TuneLab.Audio;

internal interface IAudioPlaybackHandler
{
    public static readonly string AutoDeviceName = "(Auto)";

    event Action? PlayStateChanged;
    event Action? ProgressChanged;

    event Action? CurrentDeviceChanged;
    event Action? DevicesChanged;

    bool IsPlaying { get; }

    string CurrentDriver { get; set; }
    string CurrentDevice { get; set; }

    int BufferSize { get; set; }
    int SampleRate { get; set; }
    int ChannelCount { get; set; }

    void Init(IAudioSampleProvider audioSampleProvider);
    void Destroy();

    void Start();
    void Stop();

    // [EP-MIXER-31] 丢弃当前已填充但尚未播完的块。暂停/恢复/跳转时用：否则播放头已回到“听到的位置”，
    // 设备却还在播旧块，声音会滞后一个缓冲。
    void Flush();

    IReadOnlyList<string> GetAllDevices();
    IReadOnlyList<string> GetAllDrivers();
}

// 多设备输出：UI 线程侧的副设备管理。
//
// 宿主在**编图前**把工程里被引用的副设备名交进来：播放层打开缺的、关掉不再用的，
// 返回打不开的名字。编译器据此把这些设备的路由回落 Master（见 MixerGraphCompiler）。
internal interface ISecondaryDeviceHost
{
    IReadOnlyCollection<string> SetSecondaryDevices(IReadOnlyCollection<string> deviceNames);
}
