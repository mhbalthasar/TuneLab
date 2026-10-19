namespace TuneLab.ExperimentalPatch.Abstractions;

// 补丁贡献给宿主「实验功能」菜单的一条命令。
//
// 为什么 Label 由补丁自己给：补丁层**不许**往上游翻译表里加条目（那是新的冲突面，见 AGENTS §5），
// 所以补丁按 IHostServices.UiLanguage 自己选词，宿主只负责渲染。
//
// 为什么是委托而不是"命令 id + 宿主注册表"：宿主没有补丁的命令表，也不该有；
// 一条命令的全部行为（含它要弹的窗口）都归补丁，宿主只知道"这里有个可点的条目"。
public sealed record ExperimentalCommand(string Id, string Label, Action Invoke);
