namespace TuneLab.ExperimentalPatch.MixerEngine;

// 声向法则（pan law）：把 pan 位置映射到左右增益的方式。
//
// 【为什么要有选项】线性 pan（0 dB 法则）在 pan 到正中央时总功率下降约 3 dB ——
// 左 (1−p) + 右 (1+p) 在 p = 0 时合计 2，而 p = ±1 时合计 1。等功率法则
// （−3 dB / −4.5 dB）用平方根与三角函数补偿这个凹陷，让声像移动时**音量听起来不变**。
//
// 哪个"对"没有客观答案，取决于审美：
//   · 线性（0 dB）：最直接，p = 0 时每声道 −6 dB。很多数字台与 PT 的默认。
//   · 等功率 −3 dB：p = 0 时每声道 −3 dB，功率守恒。音乐混音常用。
//   · 等功率 −4.5 dB：更激进的补偿，响度更稳但声像略窄。
//
// 【默认 0 dB】与现有行为逐位相同 —— 这是 §7.2 硬验收的要求。
// 只有当宿主显式设了别的法则时才走等功率路径。
public enum PanLaw
{
    /// <summary>线性 pan：left = 1−p、right = 1+p。p = 0 时每声道 −6 dB。</summary>
    Linear0Db = 0,

    /// <summary>等功率，中央衰减 3 dB：left = cos、right = sin（归一化后）。</summary>
    ConstantPower3Db = 3,

    /// <summary>等功率，中央衰减 4.5 dB。</summary>
    ConstantPower4_5Db = 4,
}

/// <summary>把 pan 位置与声向法则换算成左右增益。纯函数，音频线程可调用。</summary>
public static class PanLawMath
{
    /// <summary>
    /// 返回 (left, right)。pan 与 volume 已按 <c>left = volume * …</c> 的形式合入。
    /// </summary>
    public static (double Left, double Right) Resolve(double volume, double pan, PanLaw law)
    {
        // 线性 = 现有公式，单独一条路保证逐位相同。
        if (law == PanLaw.Linear0Db)
            return (volume * (1 - pan), volume * (1 + pan));

        // 等功率：pan ∈ [−1, 1] 映射到角度 ∈ [0, π/2]，再取 cos / sin。
        // p = 0 → 45°，左右都是 0.7071（= −3.01 dB）；p = ±1 → 一侧满、一侧 0。
        double angle = (pan + 1) * Math.PI / 4;
        double left = Math.Cos(angle);
        double right = Math.Sin(angle);

        // 归一化到"总功率恒定"。cos²+sin² = 1，所以乘 1/√2 让 p = 0 时
        // 每声道正好是 −3.01 dB（等功率 3 dB 的定义），且 p = ±1 时
        // 一侧 = 1/√2 × ... 不对，得让硬偏那一侧回到 1。
        //
        // 真正的等功率 pan 不归一化：p = 0 时每声道 0.7071、总功率 1.0；
        // p = ±1 时一侧 1.0、一侧 0，总功率也是 1.0。所以**什么都不用乘**。
        //
        // 那 −4.5 dB 法则怎么来？它的中央每声道 = 10^(−4.5/20) = 0.5957，
        // 比 0.7071 轻 —— 整体乘 0.5957/0.7071 = 0.8424 即可，硬偏那一侧
        // 随之变成 0.8424（−1.5 dB）。这就是"补偿更激进、声像略窄"的来源。
        if (law == PanLaw.ConstantPower4_5Db)
        {
            double scale = Math.Pow(10, -4.5 / 20) / Math.Sqrt(0.5);
            return (volume * left * scale, volume * right * scale);
        }

        return (volume * left, volume * right);
    }
}
