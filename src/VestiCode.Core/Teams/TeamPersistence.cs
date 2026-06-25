using System.Text.Json;

namespace VestiCode.Core.Teams;

/// <summary>
/// 团队定义持久化：从项目 <c>./.vesticode/teams/</c> + 全局 <c>~/.vesticode/teams/</c> 加载 JSON 定义
/// （项目同名覆盖全局）。无内置默认：没有定义文件即没有团队。运行态（任务清单/邮箱）落项目目录。
/// </summary>
public static class TeamPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string UserTeamsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesticode", "teams");

    private static string ProjectTeamsDir => Path.Combine(Directory.GetCurrentDirectory(), ".vesticode", "teams");

    public static TeamDef? LoadTeamDef(string name)
    {
        // 项目优先，回退全局。
        foreach (var dir in new[] { ProjectTeamsDir, UserTeamsDir })
        {
            var path = Path.Combine(dir, $"{name}.json");
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                return JsonSerializer.Deserialize<TeamDef>(File.ReadAllText(path), JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    public static IReadOnlyList<string> ListTeamDefs()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in new[] { UserTeamsDir, ProjectTeamsDir })
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                {
                    names.Add(Path.GetFileNameWithoutExtension(f));
                }
            }
        }
        return names.ToList();
    }

    /// <summary>团队的项目级工作目录（任务清单、邮箱存于此）。</summary>
    public static string GetTeamDir(string name)
    {
        var dir = Path.Combine(ProjectTeamsDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
