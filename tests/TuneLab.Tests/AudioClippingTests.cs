using System;
using TuneLab.Audio.NAudio;
using Xunit;

namespace TuneLab.Tests;

// 导出编码的越界封条。
//
// 事故现场：`(short)(sample * 32768)` 是**未检查转换**。sample = 1.0 得到 32768 → 回绕成 −32768，
// 超过 1 的样本更是直接符号翻转——听感上就是"刺啦刺啦"。24-bit 那条原本已经 clamp 过，
// 但"先转 int 再 clamp"在极端值上会先溢出，所以改成先夹 float。
public class AudioClippingTests
{
    static short[] ToShorts(byte[] bytes)
    {
        var values = new short[bytes.Length / 2];
        for (int i = 0; i < values.Length; i++)
            values[i] = BitConverter.ToInt16(bytes, i * 2);

        return values;
    }

    [Fact]
    public void Wav16Bit_ClampsInsteadOfWrapping()
    {
        var values = ToShorts(NAudioCodec.To16BitsBytes([1.0f, 1.5f, -1.5f, -1.0f, 0f]));

        Assert.Equal(32767, values[0]);    // 满量程正峰不该变成 −32768
        Assert.Equal(32767, values[1]);
        Assert.Equal(-32767, values[2]);
        Assert.Equal(-32767, values[3]);
        Assert.Equal(0, values[4]);
    }

    [Fact]
    public void Wav16Bit_NormalSamplesKeepTheirSign()
    {
        var values = ToShorts(NAudioCodec.To16BitsBytes([0.5f, -0.5f]));

        Assert.True(values[0] > 16000 && values[0] < 16600);
        Assert.True(values[1] < -16000 && values[1] > -16600);
    }

    [Fact]
    public void Wav24Bit_ClampsInsteadOfOverflowing()
    {
        var bytes = NAudioCodec.To24BitsBytes([1.0f, 1.5f, -1.5f, -1.0f]);

        static int Read24(byte[] data, int index)
        {
            int value = data[index * 3] | (data[index * 3 + 1] << 8) | (data[index * 3 + 2] << 16);

            // 符号扩展（24-bit 补码）。
            if ((value & 0x800000) != 0)
                value |= unchecked((int)0xFF000000);

            return value;
        }

        Assert.Equal(8388607, Read24(bytes, 0));
        Assert.Equal(8388607, Read24(bytes, 1));
        Assert.Equal(-8388607, Read24(bytes, 2));
        Assert.Equal(-8388607, Read24(bytes, 3));
    }
}
