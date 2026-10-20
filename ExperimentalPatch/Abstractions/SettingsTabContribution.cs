using System;

namespace TuneLab.ExperimentalPatch.Abstractions;

// 补丁贡献给宿主「设置」窗的一页（侧边栏一个 tab）。
//
// 【为什么这里能出现在宿主设置窗里】宿主的 tab 列表本来是硬编码的，往里加一页 = 改上游文件。
// 但"补丁的私有设置"（例如插入效果的插件扫描路径）本来就该和别设置待在一起，
// 散落在「实验功能」菜单里只会让人找不到。所以宿主留一个贡献锚点，tab 列表末尾接上补丁给的页。
//
// 【为什么 CreateContent 返回 object 而不是 Control】Abstractions 里不许出现 Avalonia 类型：
// 那份契约要保持"只有 BCL + 本层类型"，否则宿主换 Avalonia 版本时补丁层被迫跟着一起动。
// 返回值在运行时就是一个 Control，宿主负责转型（见 SettingsWindow）。
//
// Title 由补丁按 UiLanguage 自己给：补丁层**不许**往上游翻译表里加条目。
public sealed record SettingsTabContribution(string Title, Func<object> CreateContent);
