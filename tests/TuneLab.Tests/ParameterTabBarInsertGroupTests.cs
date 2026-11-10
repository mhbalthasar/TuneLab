using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using TuneLab.Data;
using TuneLab.Foundation;
using TuneLab.SDK;
using TuneLab.UI;
using Xunit;

namespace TuneLab.Tests;

// [EP-MIXER-AUTO] 二期回归：效果器侧栏给插入槽位加自动化条目后，钢琴窗参数区 tab 栏
// 必须当场多出一个按钮（插在 voice/effect 组之后）。曾出现「加了条目但 tab 栏毫无反应」，
// 这条测试直接复现整条链路：构造 -> 订阅槽位 MapModified -> 条目出现 -> 重建按钮。
public class ParameterTabBarInsertGroupTests
{
    class StubDependency : ParameterTabBar.IDependency
    {
        public event Action? ActiveAutomationChanged { add { } remove { } }
        public event Action? VisibleAutomationChanged { add { } remove { } }

        public IHolder<IMidiPart> PartHolder { get; } = new Holder<IMidiPart>();
        public void SetPart(IMidiPart part) => ((Holder<IMidiPart>)PartHolder).Set(part);
        public AutomationKey? ActiveAutomation => null;
        public bool IsAutomationVisible(AutomationKey automation) => false;
        public IActionEvent ParameterPanelVisibilityChanged { get; } = new ActionEvent();
        public bool IsParameterPanelVisible => true;
        public void AddParameterPanel() { }
        public void CloseFocusedParameterPanel() { }
        public IActionEvent WaveformVisibleChanged { get; } = new ActionEvent();
        public bool IsWaveformVisible => false;
        public void SetWaveformVisible(bool isVisible) { }
    }

    [AvaloniaFact]
    public void AddingEntry_ShowsInsertButtonInTabBar()
    {
        TestVoices.EnsureBuiltIn();   // 建 MidiPart 会构造 SoundSource，要向 VoicesManager 求声明

        var partInfo = new MidiPartInfo { Name = "p", Pos = 0, StartOffset = 0, EndOffset = 1920 };
        var trackInfo = new NativeTrackInfo { Name = "主唱" };
        trackInfo.Parts.Add(partInfo);
        var document = new ProjectDocument();
        document.SetProject(new Project(new ProjectInfo { Tracks = { trackInfo } }));

        var project = document.Project!;
        var part = (MidiPart)project.Tracks[0].Parts.First();

        var dependency = new StubDependency();
        dependency.SetPart(part);

        // 真实顺序：先打开工程 / part（那时轨道上还没有 FX），tab 栏此时已建好并完成一轮重置。
        var tabBar = new ParameterTabBar(dependency);
        Assert.Empty(InsertButtons(tabBar));

        // 之后在混音台挂上效果器（链成员变化）。这一动作必须让 tab 栏感知到新槽位，
        // 否则后续给该槽位加自动化条目时，本栏根本没有订阅过它的 Automations。
        var slot = new InsertSlot("insert-" + Guid.NewGuid().ToString("N"), "VST3", "vst3:test", "Test", (DataObject)project.Tracks[0]);
        ((DataObjectList<InsertSlot>)project.Tracks[0].Inserts).Add(slot);

        // 还没有任何插入自动化条目：不列插入按钮。
        Assert.Empty(InsertButtons(tabBar));

        // 侧栏「添加自动化」做的事：往槽位上加条目。
        var entry = new InsertSlotAutomation(slot, "3");
        entry.SetInfo(new InsertSlotAutomationInfo
        {
            ParamId = "3",
            BaseValue = 0.5,
            MinValue = 0,
            MaxValue = 1,
        });
        slot.Automations.Add("3", entry);

        // 槽位 MapModified 必须当场触发 tab 栏重建，按钮出现且名字来自条目的参数显示名。
        var buttons = InsertButtons(tabBar);
        Assert.Single(buttons);

        // 移除条目后又消失。
        slot.Automations.Remove("3");
        Assert.Empty(InsertButtons(tabBar));
    }

    // 插入组的按钮：文本里带「FX 名 · 参数名」的分隔（voice/effect 的按钮名不含「 · · 」）。
    // 这里按 AutomationKey 不可达（私有缓存），改用数量口径：构造前后差值应为 1 / -1。
    static List<ParameterButton> InsertButtons(ParameterTabBar tabBar)
    {
        var layout = tabBar.Children.OfType<WrapPanel>().Single();
        return layout.Children.OfType<ParameterButton>()
            .Where(b => (b.Text ?? string.Empty).Contains(" · "))
            .ToList();
    }
}
