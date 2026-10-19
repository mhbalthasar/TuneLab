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
}
