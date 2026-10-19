using System.Linq;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.FunctionHooker;
using Xunit;

namespace TuneLab.Tests;

// 「实验功能」菜单接缝的封条：补丁能登记命令，宿主能取到，调用不抛。
//
// 这里不建窗口（那要 Avalonia + 真工程）；只验证接缝本身：
//   · 装配补丁后命令出现在 PatchHost.GetCommands()；
//   · 没有工程时调用命令是**安静地什么都不做**，而不是抛异常。
public class MixerPatchTests
{
    sealed class StubHost : IHostServices
    {
        public string AppVersion => "0.0.0";
        public string DataFolder => System.IO.Path.GetTempPath();
        public string UiLanguage => "zh-CN";
        public void Log(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, System.Exception? exception = null) { }
    }

    [Fact]
    public void MixerPatch_ContributesMenuCommand()
    {
        var host = new StubHost();
        PatchHost.Boot(host, []);

        try
        {
            var commands = PatchHost.GetCommands();

            var mixer = commands.SingleOrDefault(command => command.Id == "mixer.open");
            Assert.NotNull(mixer);
            Assert.False(string.IsNullOrWhiteSpace(mixer!.Label));

            // 没有工程（StubHost 的 CreateMixerModel 走默认实现返回 null）→ 不该抛。
            mixer.Invoke();
        }
        finally
        {
            PatchHost.Shutdown();
        }
    }

    [Fact]
    public void CommandsAreEmptyBeforeBoot()
    {
        PatchHost.Shutdown();
        Assert.Empty(PatchHost.GetCommands());
    }
}
