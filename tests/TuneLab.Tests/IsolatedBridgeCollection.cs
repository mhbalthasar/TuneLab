using Xunit;

namespace TuneLab.Tests;

// 会**起真桥进程**的测试共用一个集合：它们操作的是同一座桥（VstBridgeClient 是进程级静态单例），
// 而"杀桥"是隔离的一部分 —— 一个测试里插件崩溃 / 超时杀掉桥，正在并行的另一个测试就会
// 看到"加载失败"。
//
// 这不是为了省时间，而是为了忠实：真实使用里同一时刻只有用户在操作的一座桥；
// 把这些测试串起来，失败才一定意味着代码有问题，而不是两条测试互相踩。
//
// 成员：BridgeIsolationTests / Vst3IntegrationTests / InsertChainTests /
// ClapIntegrationTests / ClapInsertIntegrationTests。
[CollectionDefinition("IsolatedBridge", DisableParallelization = true)]
public class IsolatedBridgeCollection;
