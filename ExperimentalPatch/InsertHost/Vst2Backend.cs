using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TuneLab.ExperimentalPatch.MixerEngine;

namespace TuneLab.ExperimentalPatch.InsertHost;

// VST2 后端：列插件清单 + 通过桥进程建实例。
//
// 【加载一律过桥】见 Vst2Insert 的类注释：进程内直载 64 位 VST2 实测会被
// 单个插件的 effSetChunk 崩掉整个宿主，所以不分位数，全部交给桥。
// 桥按插件位数选 x86 / x64 两份可执行文件（PluginBitness 读 PE 头决定）。
//
// 【扫描时"探一下"】VST2 没有静态元数据 —— 名字、厂商、是不是乐器，全在插件返回的
// AEffect 里。所以扫描（probeInstruments = true）时对每个 dll 走一次
// 桥的 加载 → 读字段 → 卸载。这个"探"同样**过桥**：在宿主进程里直载正是要避免的事
// （一个坏插件能把宿主带走）。代价是扫描慢几秒，换来三件确定的事：
//   · 乐器不会出现在混音台的「添加效果」菜单里（用户明确要求）；
//   · 菜单里是插件的真名与厂商，而不是 dll 文件名；
//   · 名字不再靠文件名猜，同名不同目录的插件靠 Locator 区分。
// 探不动（桥没起来 / 插件拒绝加载）→ 该条目照旧按文件名列出来、IsInstrument = false：
// 宁可让用户看见一个用不了的，也不要让他看不见一个能用的。
public sealed class Vst2Backend : IInsertBackend
{
    public const string FormatName = "VST2";

    string IInsertBackend.FormatName => FormatName;

    // 桥是 Windows 专属的（共享内存段 + JobObject）。别的平台上这个后端整个不可用。
    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? UnavailableReason
        => OperatingSystem.IsWindows() ? null : "the VST2 bridge is Windows-only";

    public IReadOnlyList<InsertPluginInfo> ListPlugins(IReadOnlyList<string>? directories = null,
        Action<int, int, string>? onDirectory = null, bool probeInstruments = true, Action<string>? onDetail = null)
    {
        if (directories == null || directories.Count == 0)
            return [];

        var list = new List<InsertPluginInfo>();

        // 同一个 dll 可能被两个目录同时包含（用户既有公共目录又有私有目录）：
        // 按完整路径去重，别在菜单里出现两行一模一样的。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < directories.Count; i++)
        {
            string directory = directories[i];

            // 进度先报后扫（见 IInsertBackend.ListPlugins 的说明）。
            onDirectory?.Invoke(i, directories.Count, directory);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            foreach (string file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
            {
                if (!seen.Add(file))
                    continue;

                int bitness = PluginBitness.Read(file);

                // 不是合法 PE（或既不是 32 也不是 64 位）→ 不列。列了也装不进来。
                if (bitness == 0)
                    continue;

                string name = Path.GetFileNameWithoutExtension(file);
                string vendor = string.Empty;
                bool isInstrument = false;

                if (probeInstruments)
                {
                    onDetail?.Invoke(name);

                    var probe = Probe(file);

                    if (probe != null)
                    {
                        if (!string.IsNullOrWhiteSpace(probe.Name))
                            name = probe.Name;

                        vendor = probe.Vendor ?? string.Empty;

                        // 乐器照样列进清单（缓存里留个底），由菜单按 IsInstrument 过滤掉 ——
                        // 扫描"少列一个"会让用户以为插件没扫到（DESIGN.md §9.4 的可诊断性偏好）。
                        isInstrument = probe.IsInstrument;
                    }
                }

                list.Add(new InsertPluginInfo
                {
                    Format = FormatName,

                    // 注意：**仍然是文件名**，不是上面那个显示名。
                    // PluginId 是落盘在工程里的稳定标识（.tlpx 里存的就是它），
                    // 换成本机的显示名 = 换个机器/换个插件版本就读不回旧工程。
                    PluginId = "vst2:" + Path.GetFileNameWithoutExtension(file),
                    Name = name,
                    Vendor = vendor,
                    Category = bitness + "-bit",
                    Locator = file,
                    IsInstrument = isInstrument,
                });
            }
        }

        return list;
    }

    // 探一个 dll：它是乐器还是效果器？顺手把真名与厂商带回来。
    //
    // 走与真正挂载**完全同一条路**（桥的 Load / Unload），所以这里探得动的插件，
    // 插进混音台也一定装得进去 —— 反过来也成立，探不动的不会因为"扫描时说能用"骗人。
    // 用的是临时槽位：与正在跑的插入实例互不干扰。
    //
    // 【必须有超时】`LoadLibrary` 遇到"缺依赖"会在**桥进程里**弹一个没人能点的错误框，
    // 从此不返回。扫描跑在后台线程上，那一等就是永远（界面只剩一个不动的进度条）。
    // 超时之后只能杀桥：管道里的回复已经错位，留着它会答错下一个问题。
    static ProbeResult? Probe(string file)
    {
        int bitness = PluginBitness.Read(file);
        if (bitness == 0)
            return null;

        var client = VstBridgeClient.ForBitness(bitness);

        if (!client.EnsureStartedBlocking())
            return null;

        int slot = client.AllocateSlot();

        using var timeout = new CancellationTokenSource(ProbeTimeout);

        try
        {
            var loaded = Task.Run(() => client.TryLoadAsync(slot, file, 48000, 512, bypass: true, timeout.Token))
                .GetAwaiter().GetResult();

            if (loaded is not { Ok: true })
            {
                if (timeout.IsCancellationRequested)
                    client.KillBridge();

                return null;
            }

            // 除了 effFlagsIsSynth，再补一条"没有输入或没有输出"：
            // 有些老乐器不设那个标志位，但 0 进或 0 出 — 它在总线上也放不出东西。
            bool isInstrument = loaded.IsSynth || loaded.InputChannels <= 0 || loaded.OutputChannels <= 0;

            return new ProbeResult(isInstrument, loaded.Name, loaded.Vendor);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                client.UnloadBlocking(slot);
            }
            catch (Exception)
            {
                // 卸载失败不影响清单：槽位会随桥进程一起消失。
            }
        }
    }

    // 探一个插件的上限。加载慢到 20 秒的插件，实际上就是坏了。
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    sealed record ProbeResult(bool IsInstrument, string? Name, string? Vendor);


    public IMixInsert? Create(InsertPluginInfo plugin, InsertSettings settings, out string? reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            reason = UnavailableReason;
            return null;
        }

        return Vst2Insert.Create(plugin, settings, out reason);
    }
}
