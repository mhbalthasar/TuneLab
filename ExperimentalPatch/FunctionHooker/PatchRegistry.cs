using System.Reflection;
using TuneLab.ExperimentalPatch.Abstractions;

namespace TuneLab.ExperimentalPatch.FunctionHooker;

// 补丁发现：扫本程序集里带 [Patch] 的 IPatch 实现。
// 【为什么用反射而不是一张手写清单】加补丁只加文件、不改任何注册表——注册表是双方都要编辑的
// 单点，正好是最容易和上游/别的补丁撞车的地方。扫的是我们自己的程序集，成本可忽略。
internal static class PatchRegistry
{
    public static IReadOnlyList<IPatch> Discover(IHostServices host)
    {
        var found = new List<(int Order, string Id, IPatch Patch)>();

        foreach (var type in typeof(PatchRegistry).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;

            var attribute = type.GetCustomAttribute<PatchAttribute>();
            if (attribute == null)
                continue;

            if (!attribute.Enabled)
            {
                host.Log($"patch '{type.FullName}' is disabled by [Patch(Enabled = false)]; skipped.");
                continue;
            }

            if (!typeof(IPatch).IsAssignableFrom(type))
            {
                host.LogWarning($"type '{type.FullName}' carries [Patch] but does not implement IPatch; skipped.");
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is not { } ctor)
            {
                host.LogWarning($"patch '{type.FullName}' has no parameterless constructor; skipped.");
                continue;
            }

            IPatch patch;
            try
            {
                patch = (IPatch)ctor.Invoke(null);
            }
            catch (Exception ex)
            {
                // 构造期抛错多为 TargetInvocationException（Message 只是套话）——展开内层再记。
                var real = (ex as TargetInvocationException)?.InnerException ?? ex;
                host.LogError($"patch '{type.FullName}' failed to construct", real);
                continue;
            }

            found.Add((attribute.Order, patch.Id, patch));
        }

        // Order 分组、Id 兜底：装配顺序必须每次一致，否则补丁之间的隐式依赖会随机翻车。
        return found
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Patch)
            .ToArray();
    }
}
