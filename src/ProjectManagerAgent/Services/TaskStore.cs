using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectManagerAgent.Models;

namespace ProjectManagerAgent.Services;

public class TaskStore
{
    public const string DefaultProject = "Inbox";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        
    };

    private readonly object _lock = new();
    private readonly string? _filePath;
    private readonly TimeProvider _time;
    private readonly List<TaskItem> _tasks = new();
    private int _nextId = 1;

    public TaskStore(string? filePath, TimeProvider? time = null)
    {
        _filePath = filePath;
        _time = time ?? TimeProvider.System;
        Load();
    }

    public IReadOnlyList<TaskItem> All()
    {
        lock (_lock) 
        {
            return _tasks.Select(t => t.Copy()).ToList();
        }
    }

    public TaskItem? Get(int id)
    {
        lock (_lock)
        {
            return _tasks.FirstOrDefault(t => t.Id == id)?.Copy();
        }
    }

    public IReadOnlyList<TaskItem> Subtasks(int parentId)
    {
        lock (_lock)
        {
            return _tasks.Where(t => t.ParentId == parentId).Select(t => t.Copy()).ToList();
        }
    }

    public IReadOnlyList<TaskItem> FindByName(string name, Func<TaskItem, bool>? filter = null)
    {
        var wanted = name.Trim();
        lock (_lock)
        {
            var candidates = _tasks.Where(t => filter == null || filter(t)).ToList();

            var exact = candidates
                                .Where(t => t.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                                .ToList();

            if (exact.Count > 0)
            {
                return exact.Select(t => t.Copy()).ToList();
            }
            return candidates
                        .Where(t => t.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                        .Select(t => t.Copy())
                        .ToList();
        }
    }

    public TaskItem Create(string name, string? project, Priority? priority, DateOnly? dueDate, int? parentId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Task name cannot be empty.");
        }

        lock (_lock)
        {
            TaskItem? parent = null;
            if(parentId != null)
            {
                parent = _tasks.FirstOrDefault(t => t.Id == parentId);
                if (parent == null)
                {
                    throw new ArgumentException("The Parent task does not exist.");
                }
                if (parent.ParentId != null)
                {
                    throw new ArgumentException("Subtasks cannot have their own subtasks.");
                }
            }

            var task = new TaskItem
            {
                Id = _nextId++,
                Name = name.Trim(),
                Project = ClearProject(project) ?? parent?.Project ?? DefaultProject,
                Priority = priority ?? parent?.Priority ?? Priority.Medium,
                DueDate = dueDate,
                ParentId = parentId,
                CreatedAt = _time.GetUtcNow().UtcDateTime
            };
            _tasks.Add(task);
            Save();
            return task.Copy();
        }
    }

    public TaskItem Update(int id, string? name, string? project, Priority? priority, DateOnly? dueDate, bool clearDueDate)
    {
        lock (_lock)
        {
            var task = Require(id);
            if (name != null)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("Task name cannot be empty.");
                }
                task.Name = name.Trim();
            }
            
            var newProject = ClearProject(project);
            if (newProject != null)
            {
                task.Project = newProject;
                foreach (var child in _tasks.Where(t => t.ParentId == task.Id))
                {
                    child.Project = newProject;
                }
            }
            if (priority != null)
            {
                task.Priority = priority.Value;
            }
            if (clearDueDate)
            {
                task.DueDate = null;
            }
            else if (dueDate != null)
            {
                task.DueDate = dueDate;
            }
            Save();
            return task.Copy();
        }
    }

    public TaskItem Complete(int id, bool includeSubtasks)
    {
        lock (_lock)
        {
            var task = Require(id);
            task.Completed = true;
            if (includeSubtasks)
            {
                foreach (var child in _tasks.Where(t => t.ParentId == task.Id))
                {
                    child.Completed = true;
                }
            }
            Save();
            return task.Copy();
        }
    }

    public TaskItem ToggleComplete(int id)
    {
        lock (_lock)
        {
            var task = Require(id);
            task.Completed = !task.Completed;
            Save();
            return task.Copy();
        }
    }

    public int Delete(int id)
    {
        lock (_lock)
        {
            Require(id);
            var removed = _tasks.RemoveAll(t => t.Id == id || t.ParentId == id);
            Save();
            return removed;
        }
    }

    private TaskItem Require(int id)
    {
        return _tasks.FirstOrDefault(t => t.Id == id)
                ?? throw new ArgumentException($"There is no task with Id {id}.");
    }

    private static string? ClearProject(string? project)
    {
        return string.IsNullOrWhiteSpace(project) ? null : project.Trim();
    }

    private void Load()
    {
        if (_filePath == null || !File.Exists(_filePath))
        {
            return;
        }
        
        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<TaskItem>>(json, JsonOptions) ?? new List<TaskItem>();
            _tasks.AddRange(loaded);
            _nextId = _tasks.Count == 0 ? 1 : _tasks.Max(t => t.Id) + 1;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The tasks file '{_filePath}' is not valid JSON. Fix or delete it. ({ex.Message})");
        }
    }

    private void Save()
    {
        if (_filePath == null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_tasks, JsonOptions));
        File.Move(temp, _filePath, overwrite: true);
    }
}