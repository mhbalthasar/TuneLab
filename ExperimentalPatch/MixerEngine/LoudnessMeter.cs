using System;

namespace TuneLab.ExperimentalPatch.MixerEngine;

// 一条通道的响度测量：LUFS（K 加权）+ True Peak（过采样 4×）。
//
// 【为什么两个都要】峰值表回答"会不会削波"，响度表回答"听起来多响"。人耳对低频
// 远没有对 2~5 kHz 那么敏感，所以一个低频很重的混音峰值很高、听着却不够响；
// LUFS 用 K 加权（高通 + 高频搁架）把这条曲线补回来，是广播/流媒体的标准口径
// （EBU R128 / ATSC A/85 都用它）。True Peak 则补峰表的另一个盲区：样本间的
// 波形可能超出满刻度，DA 转换时会真的削波。
//
// 【实现口径】
//   · K 加权：两级双二阶（RBJ）。第一级 4 阶高通 @ ~38 Hz（实际用两个 2 阶），
//     第二级高频搁架 @ ~1.6 kHz、+4 dB。系数按采样率算，采样率变了重算。
//   · 积分：400 ms 一块、块间 75% 重叠（gating block）。每块算均方 → 响度，
//     再用绝对门限（−70 LUFS）与相对门限（比当前积分低 10 LU）两级过滤，
//     最后对通过的块求能量平均。这是 EBU R128 的 gating，不是简单平均 ——
//     否则静音段会把读数往下拉。
//   · True Peak：4× 多相 FIR（48 抽头）插值后取绝对值。抽头数给到 48 是够的
//     （ITU-R BS.1770 的参考实现也在这个量级）。
//
// 【实时约束】音频线程每块只跑滤波 + 累加，不分配、不加锁。
// 块边界的历史值（滤波器状态、能量累加器、已通过块列表）都在实例里。
// 已通过块的索引用定长环形缓冲（容量 = 60 s @ 400 ms 块 = 150 个），
// 满了就把最旧的挤出去 —— 读数本来就是个滑动窗口，不需要无限历史。
public sealed class LoudnessMeter
{
    // ── K 加权滤波器 ────────────────────────────────────────────────
    // 两级：高通（4 阶，两个 2 阶）+ 高频搁架（2 阶）。每声道一份状态。
    readonly Biquad[] mHighPass = [new(), new()];
    readonly Biquad[] mShelf = [new(), new()];

    double mSampleRate = -1;

    // ── gating block 累加 ───────────────────────────────────────────
    // 一个 gating block = 400 ms，块间 75% 重叠 → 每 100 ms 出一个新块。
    readonly int mBlockSamples;
    readonly int mHopSamples;
    int mBlockPosition;

    double mBlockEnergyLeft;
    double mBlockEnergyRight;

    // 已完成块的能量（dB，相对 1.0 满刻度）。环形缓冲，容量 = 60 秒。
    readonly double[] mBlockLoudness;
    int mBlockWrite;
    int mBlockCount;

    // 输出：-inf..+? LUFS。null = 还没有任何块通过门限。
    double mLufs = double.NegativeInfinity;

    // True Peak（dBFS）。瞬时值，不做保持 —— 读数区会自己画保持线。
    double mTruePeakDb = double.NegativeInfinity;

    // 4× 过采样的多相 FIR：48 抽头，分成 4 个 12 抽头的子滤波器。
    static readonly double[][] PolyPhase = BuildPolyPhase();
    readonly float[] mHistoryLeft = new float[PolyTaps];
    readonly float[] mHistoryRight = new float[PolyTaps];

    const int PolyTaps = 48;
    const int PolyPhases = 4;

    public LoudnessMeter(double sampleRate = 48000)
    {
        mSampleRate = sampleRate;
        mBlockSamples = (int)Math.Round(sampleRate * 0.400);
        mHopSamples = (int)Math.Round(sampleRate * 0.100);
        mBlockLoudness = new double[600];
        RecomputeCoefficients(sampleRate);
    }

    /// <summary>采样率变了要重算系数（换设备 / 改设置时）。</summary>
    public void SetSampleRate(double sampleRate)
    {
        if (sampleRate == mSampleRate)
            return;

        mSampleRate = sampleRate;
        RecomputeCoefficients(sampleRate);

        // 块长跟着变，历史作废（不然新旧块长混在一起求平均没有意义）。
        Reset();
    }

    /// <summary>清空所有历史（换工程 / 停止播放时）。</summary>
    public void Reset()
    {
        mBlockPosition = 0;
        mBlockEnergyLeft = 0;
        mBlockEnergyRight = 0;
        mBlockWrite = 0;
        mBlockCount = 0;
        mLufs = double.NegativeInfinity;
        mTruePeakDb = double.NegativeInfinity;

        foreach (var filter in mHighPass)
            filter.Reset();
        foreach (var filter in mShelf)
            filter.Reset();

        Array.Clear(mHistoryLeft);
        Array.Clear(mHistoryRight);
    }

    /// <summary>当前 LUFS（−inf = 还没通过门限的块）。</summary>
    public double Lufs => mLufs;

    /// <summary>当前 True Peak（dBFS）。</summary>
    public double TruePeakDb => mTruePeakDb;

    /// <summary>喂一块交织立体声。音频线程可调用：不分配、不加锁。</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        if (count <= 0)
            return;

        for (int i = 0; i < count; i++)
        {
            int index = offset + i * 2;
            float left = buffer[index];
            float right = buffer[index + 1];

            // K 加权：高通 → 搁架，两级串联。
            float weightedLeft = mShelf[0].Process(mHighPass[0].Process(left));
            float weightedRight = mShelf[1].Process(mHighPass[1].Process(right));

            mBlockEnergyLeft += (double)weightedLeft * weightedLeft;
            mBlockEnergyRight += (double)weightedRight * weightedRight;

            MeasureTruePeak(left, right);

            if (++mBlockPosition < mBlockSamples)
                continue;

            // 一个 gating block 满了：收尾、进环形缓冲、重算读数。
            FinishBlock();
        }
    }

    void FinishBlock()
    {
        double energy = (mBlockEnergyLeft + mBlockEnergyRight) * 0.5 / mBlockSamples;

        // LUFS 的零点：标准里 L = −0.691 + 10·log10(能量)，满刻度 ≈ −0.69 LUFS。
        double loudness = -0.691 + 10 * Math.Log10(Math.Max(energy, 1e-24));

        mBlockLoudness[mBlockWrite] = loudness;
        mBlockWrite = (mBlockWrite + 1) % mBlockLoudness.Length;
        if (mBlockCount < mBlockLoudness.Length)
            mBlockCount++;

        // 重叠：块长 400 ms、跳 100 ms，所以只保留最后 100 ms 的能量，
        // 剩下的 300 ms 接着用（75% 重叠）。
        double keep = mHopSamples / (double)mBlockSamples;
        mBlockEnergyLeft *= keep;
        mBlockEnergyRight *= keep;
        mBlockPosition = mHopSamples;

        RecomputeLufs();
    }

    void RecomputeLufs()
    {
        if (mBlockCount == 0)
        {
            mLufs = double.NegativeInfinity;
            return;
        }

        // 环形缓冲还没绕回时，块就是按 0..mBlockCount-1 顺序写的（最旧在 0）。
        // 绕回之后，最旧的那一格正是 mBlockWrite（下一笔要写的位置），
        // 最新的在 mBlockWrite - 1。
        int Oldest(int i) => mBlockCount < mBlockLoudness.Length
            ? i
            : (mBlockWrite + i) % mBlockLoudness.Length;

        // 第一级门限：绝对 −70 LUFS。
        double sum = 0;
        int passed = 0;

        for (int i = 0; i < mBlockCount; i++)
        {
            double value = mBlockLoudness[Oldest(i)];
            if (value < AbsoluteGateLufs)
                continue;

            sum += Math.Pow(10, value / 10);
            passed++;
        }

        if (passed == 0)
        {
            mLufs = double.NegativeInfinity;
            return;
        }

        double gated = 10 * Math.Log10(sum / passed);

        // 第二级门限：相对当前积分 −10 LU。
        double relativeGate = gated + RelativeGateLufs;

        sum = 0;
        passed = 0;

        for (int i = 0; i < mBlockCount; i++)
        {
            double value = mBlockLoudness[Oldest(i)];
            if (value < relativeGate)
                continue;

            sum += Math.Pow(10, value / 10);
            passed++;
        }

        mLufs = passed == 0 ? gated : 10 * Math.Log10(sum / passed);
    }

    const double AbsoluteGateLufs = -70;
    const double RelativeGateLufs = -10;

    // ── True Peak：4× 多相 FIR ──────────────────────────────────────

    void MeasureTruePeak(float left, float right)
    {
        // 历史右移一格，把新样本放进最新位置。
        Array.Copy(mHistoryLeft, 0, mHistoryLeft, 1, PolyTaps - 1);
        Array.Copy(mHistoryRight, 0, mHistoryRight, 1, PolyTaps - 1);
        mHistoryLeft[0] = left;
        mHistoryRight[0] = right;

        // 4 个相位各出一个插值样本，取绝对值最大。
        for (int phase = 0; phase < PolyPhases; phase++)
        {
            double peak = Math.Max(
                Math.Abs(Evaluate(mHistoryLeft, phase)),
                Math.Abs(Evaluate(mHistoryRight, phase)));

            double db = 20 * Math.Log10(Math.Max(peak, 1e-9));

            if (db > mTruePeakDb)
                mTruePeakDb = db;
        }
    }

    // 多相 FIR 的第 phase 个子滤波器：抽头按相位取（phase, phase+P, phase+2P…）。
    static double Evaluate(float[] history, int phase)
    {
        double sum = 0;
        for (int tap = 0; tap < PolyTaps / PolyPhases; tap++)
            sum += PolyPhase[phase][tap] * history[tap * PolyPhases];

        return sum;
    }

    // 4× 插值的窗 sinc 低通（Kaiser 窗，β = 8）。
    // 系数在编译期算一次，运行时只读。
    static double[][] BuildPolyPhase()
    {
        var phases = new double[PolyPhases][];
        int tapsPerPhase = PolyTaps / PolyPhases;
        double beta = 8.0;

        for (int phase = 0; phase < PolyPhases; phase++)
        {
            var coefficients = new double[tapsPerPhase];

            for (int tap = 0; tap < tapsPerPhase; tap++)
            {
                // 连续抽头位置（含相位偏移），中心在 (PolyTaps−1)/2。
                double position = tap * PolyPhases + phase - (PolyTaps - 1) / 2.0;

                // 归一化 sinc：截止频率 = 1/(2·过采样率)
                double x = position / PolyPhases;
                double sinc = Math.Abs(x) < 1e-9
                    ? 1.0
                    : Math.Sin(Math.PI * x) / (Math.PI * x);

                coefficients[tap] = sinc * Kaiser(position / (PolyTaps / 2.0), beta);
            }

            phases[phase] = coefficients;
        }

        return phases;
    }

    static double Kaiser(double x, double beta)
    {
        if (Math.Abs(x) >= 1)
            return 0;

        double argument = beta * Math.Sqrt(1 - x * x);
        return BesselZero(argument) / BesselZero(beta);
    }

    static double BesselZero(double x)
    {
        double sum = 1;
        double term = 1;
        double half = x / 2;

        for (int k = 1; k < 24; k++)
        {
            term *= (half * half) / (k * k);
            sum += term;
            if (term < 1e-12 * sum)
                break;
        }

        return sum;
    }

    // ── K 加权系数 ──────────────────────────────────────────────────

    // BS.1770-4 公布的 48 kHz 系数（"K" 加权的两级）。别的采样率按
    // 双线性变换的预 warp 重新算 —— 直接把 48 kHz 的数字频率映射到新采样率
    // 会因频率折叠而失真（44.1 kHz 时截止频率会偏低约 8%）。
    //
    // 第一级（高通，~38 Hz、4 阶）与第二级（高频搁架，~1.68 kHz、+4 dB）。
    // 注意搁架的 b0 > 1（标准没有把 a0 归一成 1），这不是笔误。
    static readonly double[] ShelfB = [1.53512485958697, -2.69169618940638, 1.19839281085285];
    static readonly double[] ShelfA = [-1.69065929318241, 0.73248077421585];

    // 高通：BS.1770 给的是 2 阶级联，两个 2 阶的系数相同（Q ≈ 0.5）。
    static readonly double[] HighPassB = [1.0, -2.0, 1.0];
    static readonly double[] HighPassA = [-1.99004745483398, 0.99007225036621];

    const double ReferenceSampleRate = 48000;

    void RecomputeCoefficients(double sampleRate)
    {
        Rescale(mHighPass[0], HighPassB, HighPassA, sampleRate);
        Rescale(mHighPass[1], HighPassB, HighPassA, sampleRate);
        Rescale(mShelf[0], ShelfB, ShelfA, sampleRate);
        Rescale(mShelf[1], ShelfB, ShelfA, sampleRate);
    }

    // 把 48 kHz 的系数变换到另一个采样率：先反解出模拟域参数（k、Q、vh、vb、vl），
    // 再按新采样率重新算数字系数。这是 BS.1770 附录里给出的标准做法。
    static void Rescale(Biquad filter, double[] b48, double[] a48, double sampleRate)
    {
        if (sampleRate == ReferenceSampleRate)
        {
            filter.B0 = b48[0];
            filter.B1 = b48[1];
            filter.B2 = b48[2];
            filter.A1 = a48[0];
            filter.A2 = a48[1];
            return;
        }

        double a1 = a48[0];
        double a2 = a48[1];
        double b0 = b48[0];
        double b1 = b48[1];
        double b2 = b48[2];

        // 反解（BS.1770 附录公式）
        double kOverQ = (2 - 2 * a2) / (a2 - a1 + 1);
        double k = Math.Sqrt((a1 + a2 + 1) / (a2 - a1 + 1));
        double q = k / kOverQ;
        double vb = (b0 - b2) / (1 - a2);
        double vh = (b0 - b1 + b2) / (a2 - a1 + 1);
        double vl = (b0 + b1 + b2) / (a1 + a2 + 1);

        // 频率预 warp：tan(atan(k) * fs_old / fs_new)
        double warped = Math.Tan(Math.Atan(k) * ReferenceSampleRate / sampleRate);
        double kSq = warped * warped;
        double common = 1 / (1 + (warped / q) + kSq);

        filter.A1 = 2 * (kSq - 1) * common;
        filter.A2 = (1 - (warped / q) + kSq) * common;
        filter.B0 = (vh + (vb * warped / q) + (vl * kSq)) * common;
        filter.B1 = 2 * (vl * kSq - vh) * common;
        filter.B2 = (vh - (vb * warped / q) + (vl * kSq)) * common;
    }
    // 双二阶（Direct Form I）。每声道一个实例，状态跟着声道走。
    sealed class Biquad
    {
        public double B0, B1, B2, A1, A2;
        double mX1, mX2, mY1, mY2;

        public float Process(float input)
        {
            double x = input;
            double y = B0 * x + B1 * mX1 + B2 * mX2 - A1 * mY1 - A2 * mY2;

            mX2 = mX1;
            mX1 = x;
            mY2 = mY1;
            mY1 = y;

            return (float)y;
        }

        public void Reset()
        {
            mX1 = mX2 = mY1 = mY2 = 0;
        }
    }
}


