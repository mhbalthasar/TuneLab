using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TuneLab.Audio;
using TuneLab.Data;
using TuneLab.ExperimentalPatch.Abstractions;
using TuneLab.ExperimentalPatch.InsertHost;
using TuneLab.Foundation;
using TuneLab.GUI;
using TuneLab.GUI.Components;
using TuneLab.I18N;
using TuneLab.Utils;
using Button = TuneLab.GUI.Components.Button;

namespace TuneLab.UI;

// [EP-MIXER-AUTO] 插入参数自动化面板：效果器侧栏下半区。
//
// 一条通道的插入效果器（VST3 / VST2 / CLAP / JSFX）的线性参数可以挂自动化曲线：
// 「+ 添加」弹出三级菜单 —— 通道（当前音频流经的、带效果器的）→ 效果器 → 线性参数；
// 选定后在列表里加一条，曲线数据挂在对应的 InsertSlot 上（undo / 落盘 / 随槽位走）。
//
// 【加性语义】曲线存的是偏移量：实际生效值 = clamp(基值 + 偏移(tick), 参数 Min, 参数 Max)。
// 基值 = 参数面板里的值；自动化接管期间面板改动 = 改基值（见
// InsertInstanceFactory.TryWriteAutomatedParameter）。插件 GUI 里的改动拦不到，会在下一块
// 被自动化覆盖 —— v1 的已知限制。
//
// 【音频】曲线编辑实时生效：条目增删 → 重建混音图（绑定在编译期建）；曲线锚点变化走
// 冻结快照换引用（Binding），播放中下一块就听到，不必重建图。
//
// 【本期的边界】曲线锚点在侧栏内编辑（列表行展开的锚点表）；钢琴窗参数区的 lane 集成
// 是二期。undo/redo 增删条目时混音图在下一次重建前沿用旧绑定（曲线值的 undo 会经快照
// 换引用即时生效，不受此限）。
internal class InsertAutomationsController : StackPanel
{
    public InsertAutomationsController()
    {
        Orientation = Orientation.Vertical;
        Background = Style.INTERFACE.ToBrush();

        Children.Add(BuildHeader());
        Children.Add(mRows);
    }

    // 与 EffectsController 同一套目标下发（EffectsSideBarContentProvider 调）。
    public void SetParts(IReadOnlyList<IMidiPart> parts)
    {
        s.DisposeAll();
        mParts = parts;
        Rebuild();
    }

    // ── 静态结构 ──

    readonly StackPanel mRows = new() { Orientation = Orientation.Vertical };
    IReadOnlyList<IMidiPart> mParts = [];
    readonly DisposableManager s = new();

    // 每次 Rebuild 各自重建的订阅（跟着当前通道集合走）。
    readonly DisposableManager mRowSubscriptions = new();

    Control BuildHeader()
    {
        // 侧栏的分组标题沿用 AttributeController 那套字面（小字、分组线上方）。
        var title = new TextBlock
        {
            Text = "轨道效果器".Tr(TC.Property),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Style.LIGHT_WHITE.ToBrush(),
            Margin = new(24, 12, 8, 4),
        };

        var add = MakeTextButton("+ " + "Add Automation".Tr(TC.Property), 0);
        add.Height = 30;
        add.Margin = new(24, 8);
        add.Clicked += OnAddClicked;
        mAddButton = add;

        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(new Border { Height = 1, Background = Style.BACK.ToBrush() });
        panel.Children.Add(title);
        panel.Children.Add(add);
        return panel;
    }

    Button? mAddButton;

    // ── 列表 ──

    void Rebuild()
    {
        mRowSubscriptions.DisposeAll();
        mRows.Children.Clear();

        var track = SingleTrack();
        bool singleTrack = track != null;

        mAddButton!.IsVisible = singleTrack;

        if (!singleTrack)
            return;

        var project = track!.Project as Project;
        if (project == null)
            return;

        // 结构订阅：通道集合 / 链成员 / 条目成员变化 → 重建列表。
        // （条目成员变化同时要重建混音图 —— 绑定是编译期建的。）
        SubscribeMembership((DataObjectList<InsertSlot>)track.Inserts);
        SubscribeMembership((DataObjectList<InsertSlot>)project.MasterInserts);
        foreach (var aux in project.Auxes)
            SubscribeMembership((DataObjectList<InsertSlot>)aux.Inserts);

        foreach (var channel in CollectFlowChannels(track, project))
        {
            foreach (var slot in channel.Slots())
            {
                foreach (var kvp in slot.Automations)
                {
                    var view = new InsertAutomationRow(channel, slot, kvp.Value);
                    view.Removed += OnEntryRemoved;
                    mRows.Children.Add(view.Root);
                }
            }
        }
    }

    void SubscribeMembership(DataObjectList<InsertSlot> slots)
    {
        slots.MembershipModified.Subscribe(Rebuild, mRowSubscriptions);

        foreach (var slot in slots)
            slot.Automations.MapModified.Subscribe(OnSlotAutomationsChanged, mRowSubscriptions);
    }

    // 条目成员变化（增删 / undo / redo）：曲线绑定跟着槽位集合走，图必须重建。
    void OnSlotAutomationsChanged()
    {
        AudioEngine.RebuildMixerGraph();
        Rebuild();
    }

    void OnEntryRemoved(InsertSlot slot, InsertSlotAutomation entry)
    {
        slot.Automations.Remove(entry.ParamId);
        mParts[0].Commit();
        AudioEngine.RebuildMixerGraph();
        Rebuild();
    }

    ITrack? SingleTrack()
    {
        if (mParts.Count == 0)
            return null;

        var track = mParts[0].Track;

        foreach (var part in mParts)
        {
            if (!ReferenceEquals(part.Track, track))
                return null;
        }

        return track;
    }

    // ── 三级菜单 ──

    void OnAddClicked()
    {
        var track = SingleTrack();
        if (track?.Project is not Project project)
            return;

        mAddButton?.OpenContextMenu(BuildChannelMenu(track, project));
    }

    ContextMenu BuildChannelMenu(ITrack track, Project project)
    {
        var menu = new ContextMenu();

        var channels = CollectFlowChannels(track, project);

        if (channels.Count == 0)
        {
            menu.Items.Add(DisabledItem("(No Effects)"));
            return menu;
        }

        foreach (var channel in channels)
        {
            var fxItems = new List<MenuItem>();

            foreach (var slot in channel.Slots())
            {
                if (slot.IsBypassed.Value)
                    continue;   // 旁通的 FX 音频不过：给它挂自动化只会让人以为"没反应"

                var parameterMenu = BuildParameterMenu(slot);
                if (parameterMenu == null)
                    continue;

                var fxItem = new MenuItem { Header = SlotDisplayName(channel, slot) };
                fxItem.Items.Add(parameterMenu);
                fxItems.Add(fxItem);
            }

            if (fxItems.Count == 0)
            {
                menu.Items.Add(DisabledItem(channel.MenuName + " — " + "没有可自动化的参数".Tr(TC.Property)));
                continue;
            }

            var channelItem = new MenuItem { Header = channel.MenuName };

            foreach (var fxItem in fxItems)
                channelItem.Items.Add(fxItem);

            menu.Items.Add(channelItem);
        }

        return menu;
    }

    // 该槽位的参数三级菜单；没有可用参数（没活实例 / 全是选择型）回 null（一级菜单降级）。
    ContextMenu? BuildParameterMenu(InsertSlot slot)
    {
        if (!InsertInstanceFactory.Shared.TryGetLive(slot.Id, out var live) || live == null)
            return null;

        var menu = new ContextMenu();
        bool any = false;

        foreach (var parameter in MixerAutomation.GetLiveParameters(live))
        {
            if (parameter.MinValue >= parameter.MaxValue)
                continue;

            // 选择型（枚举 / 步进）不进自动化：曲线是连续插值，离散跳变没有意义。
            if (parameter.IsDiscrete)
                continue;

            if (slot.Automations.ContainsKey(parameter.Id.ToString()))
                continue;   // 已经挂了自动化：菜单里不再列，列表里去编辑

            var capturedSlot = slot;
            var capturedParameter = parameter;
            menu.Items.Add(new MenuItem()
                .SetName(parameter.Name)
                .SetAction(() => AddEntry(capturedSlot, capturedParameter)));
            any = true;
        }

        return any ? menu : null;
    }

    void AddEntry(InsertSlot slot, InsertParameter parameter)
    {
        string key = parameter.Id.ToString();
        if (slot.Automations.ContainsKey(key))
            return;

        var entry = new InsertSlotAutomation(slot, key);
        entry.SetInfo(new InsertSlotAutomationInfo
        {
            ParamId = key,
            BaseValue = parameter.CurrentValue,   // 基值 = 当前参数值（叠加的起点）
            // 量程抄自活实例：钢琴窗 lane 的值轴 / 钳位不依赖实例活着（重开工程也能画轴）。
            MinValue = parameter.MinValue,
            MaxValue = parameter.MaxValue,
            Points = [],
        });
        slot.Automations.Add(key, entry);

        mParts[0].Commit();               // 一步 undo：加条目 + 基值
        AudioEngine.RebuildMixerGraph();  // 绑定是编译期建的
        Rebuild();
    }

    // ── 音流通道枚举 ──

    // 从当前轨道出发，沿 output / send 收集音频真正流经的通道；只列带效果器的。
    // 没效果器的通道照样往下走（它的下游仍可能带效果器）。
    internal sealed class FlowChannel(string menuName, Func<IReadOnlyList<InsertSlot>> slots)
    {
        // 一级菜单的条目文案（"Track:轨道名" / "Aux:通道名" / "Master"）。
        public readonly string MenuName = menuName;
        public readonly Func<IReadOnlyList<InsertSlot>> Slots = slots;
    }

    internal static List<FlowChannel> CollectFlowChannels(ITrack track, Project project)
    {
        var channels = new List<FlowChannel>();
        var visited = new HashSet<string>();

        void VisitRoute(MixerRoute route)
        {
            switch (route.Kind)
            {
                case MixerOutputKind.Master:
                    Visit("master", "Master", "Master", MixerRoute.Master, [], () => project.MasterInserts);
                    break;

                case MixerOutputKind.Aux:
                    var aux = project.Auxes.FirstOrDefault(a => a.Id == route.Id);
                    if (aux != null)
                        Visit(aux.Id, "Aux:" + aux.Name.Value, aux.Name.Value, aux.Output.Value, aux.Sends,
                            () => aux.Inserts);
                    break;
            }
        }

        void Visit(string id, string menuName, string displayName, MixerRoute output,
            IReadOnlyList<Send> sends, Func<IReadOnlyList<InsertSlot>> slots)
        {
            if (!visited.Add(id))
                return;

            if (slots().Count > 0)
                channels.Add(new FlowChannel(menuName, slots));

            VisitRoute(output);
            foreach (var send in sends)
                VisitRoute(send.Target.Value);
        }

        Visit(MixerTrackIds.IdOf(track), "Track:" + track.Name.Value, track.Name.Value,
            track.Output.Value, track.Sends, () => track.Inserts);

        return channels;
    }

    static string SlotDisplayName(FlowChannel channel, InsertSlot slot)
    {
        // 同一条链上同名 FX 挂了两份时加序号，条目名才不会撞。
        var slots = channel.Slots();
        int count = 0;
        int index = 0;

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Name.Value != slot.Name.Value)
                continue;

            count++;
            index = count;
        }

        return count > 1 ? $"{slot.Name.Value} #{index}" : slot.Name.Value;
    }

    static MenuItem DisabledItem(string header) => new() { Header = header, IsEnabled = false };

    static Button MakeTextButton(string text, double width)
    {
        var button = new Button();
        if (width > 0)
            button.Width = width;
        button.AddContent(new() { Item = new BorderItem() { CornerRadius = 4 }, ColorSet = new() { Color = Style.BUTTON_NORMAL, HoveredColor = Style.BUTTON_NORMAL_HOVER, PressedColor = Style.INTERFACE } });
        button.AddContent(new() { Item = new TextItem() { Text = text, FontSize = 12 }, ColorSet = new() { Color = Colors.White } });
        return button;
    }

    // ── 一行：条目名 + 展开编辑 + 删除 ──

    sealed class InsertAutomationRow
    {
        public InsertAutomationRow(FlowChannel channel, InsertSlot slot, InsertSlotAutomation entry)
        {
            mChannel = channel;
            mSlot = slot;
            mEntry = entry;

            // 行样式沿用侧栏字段行的那套 TuneLab 控件：Label（文字居中、过长省略）+
            // TuneLab Button（BorderItem/TextItem 配色），不用 Avalonia 原生样式。
            var name = new Label
            {
                Height = 30,
                FontSize = 12,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = Style.TEXT_LABEL.ToBrush(),
                Content = EntryName(),
                Padding = new(24, 0),
            };

            var remove = MakeTextButton("✕", 24);
            remove.Height = 22;
            remove.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            remove.Margin = new(0, 0, 24, 0);
            remove.Clicked += () => Removed?.Invoke(mSlot, mEntry);

            var header = new DockPanel { Height = 30, LastChildFill = true };
            DockPanel.SetDock(remove, Dock.Right);
            header.Children.Add(remove);
            header.Children.Add(name);

            mRoot = new StackPanel { Orientation = Orientation.Vertical };
            mRoot.Children.Add(header);
            mRoot.Children.Add(new Border { Height = 1, Background = Style.BACK.ToBrush(), Margin = new(24, 0, 24, 0) });
        }

        public Control Root => mRoot;
        public event Action<InsertSlot, InsertSlotAutomation>? Removed;

        string EntryName()
        {
            // 条目名 = E + 通道 kind（+ 通道名）+ FX 名 + 参数名。参数的显示名按活实例
            // 现查（名字不落盘，只存 id）；实例不在了退到 "#id"。
            string menuName = mChannel.MenuName;
            string kind = menuName.Split(':')[0];
            string channelPart = kind == "Master" ? "EMaster" : $"{kind}:{menuName[(kind.Length + 1)..]}";

            return $"{channelPart}:{mSlot.Name.Value}:{ParameterName()}";
        }

        string ParameterName()
        {
            if (InsertInstanceFactory.Shared.TryGetLive(mSlot.Id, out var live) && live != null)
            {
                if (long.TryParse(mEntry.ParamId, out long id))
                {
                    var parameter = MixerAutomation.GetLiveParameters(live).FirstOrDefault(p => p.Id == id);
                    if (parameter != null && !string.IsNullOrWhiteSpace(parameter.Name))
                        return parameter.Name;
                }
            }

            return "#" + mEntry.ParamId;
        }

        readonly FlowChannel mChannel;
        readonly InsertSlot mSlot;
        readonly InsertSlotAutomation mEntry;
        readonly StackPanel mRoot;
    }
}
