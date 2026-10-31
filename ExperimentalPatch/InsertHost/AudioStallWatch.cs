namespace TuneLab.ExperimentalPatch.InsertHost;

// 运行期"音频路彻底停用"的判定：连续若干块拿不到桥的结果，就认定桥赶不上了。
//
// 【为什么从 VstBridgeClient 抽出来】这是"运行期挂起"这件事唯一能脱离真桥单测的地方 ——
// 给计数器喂失败，就能钉住"到第几块判死 / 杀桥后复位"两条行为（与 BridgeTimeout 同一套纪律）。
// 真的起进程验证由 opt-in 的真插件回归做，这里保证阈值与复位的语义不会再漂。
//
// 【为什么不是"每块都重试"】桥卡死（插件死循环 / 进程被挂起）时每块都要白等 20ms，
// 而 SDL 的缓冲只有 21ms —— 那会把音频拖成断续爆音，比"这个插件在你这台机器上不生效"糟得多。
// 停掉之后旁通 / 移除这个效果器即可，混音台其余部分照常。
//
// 【实时纪律】ReportFailure 在音频线程调用；Stalled 会被 UI 线程读，所以用 volatile。
internal sealed class AudioStallWatch
{
    // 连续多少块失败就认定桥赶不上了：20ms/块 × 50 ≈ 连续 1 秒都拿不到结果。
    public const int FailureLimit = 50;

    int mFailures;
    volatile bool mStalled;

    /// <summary>桥的音频路是否已停用（连续失败过多）。音频线程与 UI 线程都会读。</summary>
    public bool Stalled => mStalled;

    /// <summary>
    /// 记一次音频块失败；返回**本次**是否刚跨到停滞态（调用方据此只记一次日志）。
    /// 已停滞后再调不再累加，总是返回 false。
    /// </summary>
    public bool ReportFailure()
    {
        if (mStalled)
            return false;

        if (++mFailures < FailureLimit)
            return false;

        mStalled = true;
        return true;
    }

    /// <summary>
    /// 一次成功的块：把失败计数清零。停滞态下不会走到这里（调用方提前直通了），
    /// 所以停在成功路径上的 Reset 不会误清真正的停滞。
    /// </summary>
    public void ReportSuccess()
    {
        mFailures = 0;
    }

    /// <summary>桥换了（重启 / 关闭）时清零：之前那串"赶不上"不能算到新桥头上。</summary>
    public void Reset()
    {
        mFailures = 0;
        mStalled = false;
    }
}
