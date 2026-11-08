using System;

namespace TuneLab.ExperimentalPatch.Abstractions;

// 补丁贡献给宿主「导出」侧栏的一段选项。
//
// 【为什么需要这个锚点】导出侧栏是宿主 UI，但“分轨导出是否带轨道效果器”是补丁功能。
// 直接改宿主侧栏 = 把补丁代码写回上游文件；这里只让宿主留一个挂载点：
// 宿主画标题、把 CreateContent 返回的控件塞进侧栏，导出时按宿主自己的 ExportOptions 走。
//
// 【为什么 CreateContent 返回 object】Abstractions 里不许出现 Avalonia 类型。
// 运行时返回的就是一个 Control，宿主负责转型（与 SettingsTabContribution 同一套做法）。
//
// Refresh 由宿主在切换工程时调用：补丁控件据此重新读取 IHostServices 上的当前工程状态。
public sealed record ExportOptionsContribution(string Title, Func<object> CreateContent, Action Refresh);
