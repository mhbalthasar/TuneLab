namespace TuneLab.ExperimentalPatch.Abstractions;

// 标记一个可被自动发现的补丁。没有它就不会被发现（= 快速停用：摘掉特性即可，不必删代码）。
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PatchAttribute : Attribute
{
    // 同序号按 Id 字典序兜底，保证每次启动装配顺序一致（顺序敏感时显式给 Order）。
    public int Order { get; init; }

    // false = 发现时跳过（保留代码但不启用）。
    public bool Enabled { get; init; } = true;
}
