namespace VestiCode.Core.Commands;

/// <summary>斜杠命令注册中心：注册（含别名冲突检测）、查找、补全。</summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册命令；命名或别名冲突抛 <see cref="InvalidOperationException"/>。</summary>
    public void Register(ICommand command)
    {
        if (!_commands.TryAdd(command.Name, command))
        {
            throw new InvalidOperationException($"命令名冲突: /{command.Name}");
        }
        foreach (var alias in command.Aliases)
        {
            if (_aliases.ContainsKey(alias) || _commands.ContainsKey(alias))
            {
                throw new InvalidOperationException($"别名冲突: /{alias}");
            }
            _aliases[alias] = command.Name;
        }
    }

    public void RegisterRange(IEnumerable<ICommand> commands)
    {
        foreach (var command in commands)
        {
            Register(command);
        }
    }

    /// <summary>按名或别名查找（大小写不敏感）。</summary>
    public ICommand? Lookup(string name)
    {
        if (_commands.TryGetValue(name, out var cmd))
        {
            return cmd;
        }
        return _aliases.TryGetValue(name, out var canonical) ? _commands[canonical] : null;
    }

    /// <summary>所有非隐藏命令（按名排序）。</summary>
    public IReadOnlyList<ICommand> ListVisible() =>
        _commands.Values.Where(c => !c.Hidden).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

    /// <summary>返回以 <paramref name="prefix"/> 开头的命令补全（带前导 /）。</summary>
    public IReadOnlyList<string> GetCompletions(string prefix)
    {
        var p = prefix.TrimStart('/');
        var results = new List<string>();
        foreach (var cmd in _commands.Values)
        {
            if (cmd.Hidden)
            {
                continue;
            }
            if (cmd.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                results.Add("/" + cmd.Name);
            }
            results.AddRange(cmd.Aliases
                .Where(a => a.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                .Select(a => "/" + a));
        }
        results.Sort(StringComparer.Ordinal);
        return results;
    }
}
