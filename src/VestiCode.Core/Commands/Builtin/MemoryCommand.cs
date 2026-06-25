using System.Text;
using VestiCode.Core.Notes;

namespace VestiCode.Core.Commands.Builtin;

/// <summary>/memory — 查看自动笔记（用户偏好 / 纠正反馈 / 项目知识 / 参考资料）。</summary>
public sealed class MemoryCommand(AutoNoteManager notes) : ICommand
{
    private static readonly string[] AllCategories = ["用户偏好", "纠正反馈", "项目知识", "参考资料"];

    public string Name => "memory";
    public IReadOnlyList<string> Aliases => ["notes"];
    public string Description => "查看自动记录的长期笔记";
    public string Usage => "/memory [分类]   例：/memory 项目知识";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        // 兼容 "/memory show <分类>" 与 "/memory <分类>"。
        var category = args.FirstOrDefault(a => !a.Equals("show", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(category))
        {
            if (!AllCategories.Contains(category))
            {
                return Task.FromResult(CommandResult.Show($"未知分类: {category}。可用: {string.Join(" / ", AllCategories)}"));
            }
            return Task.FromResult(CommandResult.Show($"### {category}\n{notes.ReadNote(category)}"));
        }

        var sb = new StringBuilder();
        foreach (var cat in AllCategories)
        {
            sb.Append($"### {cat}\n{notes.ReadNote(cat)}\n\n");
        }
        return Task.FromResult(CommandResult.Show(sb.ToString().TrimEnd()));
    }
}
