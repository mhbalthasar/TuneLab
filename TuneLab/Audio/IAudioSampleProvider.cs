using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TuneLab.Audio;

internal interface IAudioSampleProvider
{
    void Read(float[] buffer, int offset, int count);

    // [EP-MIXER-31] SDL 回调刚把 playedFrames 帧写入设备流；这些帧是**接下来**要播的，
    // 所以它是"已经播完的绝对帧数"（回调前累计值），供播放头对齐使用。
    void OnSamplesConsumed(long playedFrames);
}
