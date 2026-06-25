using System.Text.Json;
using System.Text.Json.Serialization;

namespace VestiCode.Core.Teams;

/// <summary>团队共享任务清单（JSON 持久化，支持依赖）。</summary>
public sealed class SharedTaskList
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;
    private readonly Dictionary<string, TeamTask> _tasks = new(StringComparer.Ordinal);

    public SharedTaskList(string teamDir)
    {
        _file = Path.Combine(teamDir, "tasks.json");
        Load();
    }

    public TeamTask Create(string name, string description = "", List<string>? dependsOn = null)
    {
        var task = new TeamTask { Name = name, Description = description, DependsOn = dependsOn ?? [] };
        _tasks[task.Id] = task;
        Save();
        return task;
    }

    public TeamTask? Get(string id) => _tasks.GetValueOrDefault(id);

    public IReadOnlyList<TeamTask> ListAll(TeamTaskStatus? status = null) =>
        _tasks.Values.Where(t => status is null || t.Status == status).OrderBy(t => t.CreatedAt).ToList();

    public TeamTask? Update(string id, TeamTaskStatus? status = null, string? result = null, string? assignedTo = null)
    {
        if (!_tasks.TryGetValue(id, out var task))
        {
            return null;
        }
        if (status is { } s)
        {
            task.Status = s;
        }
        if (result is not null)
        {
            task.Result = result;
        }
        if (assignedTo is not null)
        {
            task.AssignedTo = assignedTo;
        }
        task.UpdatedAt = DateTimeOffset.UtcNow;
        Save();
        return task;
    }

    public TeamTask? Assign(string id, string member) => Update(id, TeamTaskStatus.InProgress, assignedTo: member);

    public TeamTask? Complete(string id, string result = "") => Update(id, TeamTaskStatus.Completed, result);

    /// <summary>依赖全部完成、且未分配的待办任务。</summary>
    public IReadOnlyList<TeamTask> ReadyTasks() =>
        _tasks.Values.Where(t =>
            t.Status == TeamTaskStatus.Pending &&
            string.IsNullOrEmpty(t.AssignedTo) &&
            t.DependsOn.All(d => _tasks.GetValueOrDefault(d)?.Status == TeamTaskStatus.Completed)).ToList();

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(_tasks, JsonOptions));
    }

    private void Load()
    {
        if (!File.Exists(_file))
        {
            return;
        }
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, TeamTask>>(File.ReadAllText(_file), JsonOptions);
            if (data is not null)
            {
                foreach (var (k, v) in data)
                {
                    _tasks[k] = v;
                }
            }
        }
        catch (JsonException)
        {
            // 损坏则忽略
        }
    }
}
