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
}
