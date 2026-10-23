using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TuneLab.Data;
using TuneLab.Foundation;

namespace TuneLab.Audio;

internal interface IAudioTrack
{
    bool IsMute { get; }
    bool IsSolo { get; }

    // [EP-MIXER-25] Solo 安全：别人 solo 时这条通道不被压制。
    // 默认 false —— 没实现这个能力的实现者行为不变。
    bool IsSoloSafe => false;
    double Volume { get; }
    double Pan { get; }
    double EndTime { get; }
    IEnumerable<IAudioSource> AudioSources { get; }

    // 路由（DESIGN.md §4.2 / §4.3）：输出去哪、并联了哪些 send。
    // 放在这里而不是只放 Data 层：混音图要按它连边，而图只认识 IAudioTrack。
    MixerRoute Output { get; }
    IReadOnlyList<Send> Sends { get; }

    // 串联插入链（DESIGN.md §5）。同理：图只认识 IAudioTrack，插入要按它编进节点。
    //
    // 形状必须与 ITrack.Inserts **逐字相同**（IReadOnlyDataObjectList<InsertSlot>）：
    // Track 同时实现两个接口，而 C# 要求显式实现的返回类型能同时满足双方 ——
    // 写成 IReadOnlyList<> 会以 CS0738 拒绝编译。
    IReadOnlyDataObjectList<InsertSlot> Inserts { get; }
    // [EP-MIXER-20] 输入级：混进通道缓冲之后、推子与插入之前（DESIGN §5.7.22）。
    // 默认值（0 / false / 0）下引擎整段跳过，所以"没有输入级"与旧路径逐位相同。
    double TrimDb => 0;
    bool PhaseInvert => false;
    double HpfHz => 0;

}

