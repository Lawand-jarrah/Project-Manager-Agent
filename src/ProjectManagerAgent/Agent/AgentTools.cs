using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;
using ProjectManagerAgent.Models;
using ProjectManagerAgent.Services;

namespace ProjectManagerAgent.Agent;

public class AgentTools
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly TaskStore _store;
    private readonly TimeProvider _time;

    public AgentTools(TaskStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    public static IReadOnlyList<ChatTool> Definitions { get; } = new List<ChatTool>
    {
        ChatTool.CreateFunctionTool(
            "list_tasks",
            "Use this tool when you need to list all existing tasks in the project. By default, it lists open tasks. Combine the filters to narrow down the results.",
            BinaryData.FromString("""
            {
                "type": "object",
                "properties": {
                    "project": { "type": "string", "description": "The name of the project to list tasks for." },
                    "priority": { "type": "string", "enum": ["low", "medium", "high"] },
                    "status": { "type": "string", "enum": ["open", "completed", "all"], "description": "Defaults to open." },
                    "dueWithin": { "type": "string", "enum": ["today", "week", "overdue", "later"], "description": "The time frame within which tasks are due." }
                },
            "additionalProperties": false
            }
            """)),

        ChatTool.CreateFunctionTool(
            "create_task",
            "Use this tool when you need to create a new task in the project. Give a 'Parent' to make it a subtask of an existing task.",
            BinaryData.FromString("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string" },
                    "project": { "type": "string", "description": "Default to inbox, or the parent's project." },
                    "priority": { "type": "string", "enum": ["low", "medium", "high"] },
                    "dueDate": { "type": "string", "description": "Format:YYYY-MM-DD." },
                    "parent": { "type": "string", "description": "Name of an existing task to nest this one under." }
                },
                "required": ["name"],
                "additionalProperties": false
            }
            """)),

        ChatTool.CreateFunctionTool(
            "update_task",
            "Change the details of an existing task. Rename it, change its priority, due date, or its project.",
            BinaryData.FromString("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string", "description": "The current name of the task." },
                    "newName": { "type": "string", "description": "The new name for the task." },
                    "project": { "type": "string", "description": "The project the task belongs to." },
                    "priority": { "type": "string", "enum": ["low", "medium", "high"] },
                    "dueDate": { "type": "string", "description": "Format:YYYY-MM-DD, or 'none' to remove the due date." }
                },
                "required": ["name"],
                "additionalProperties": false
            }
            """)),

        ChatTool.CreateFunctionTool(
            "complete_task",
            "Use this tool to mark a task as done. If it has an open subtask, the tool asks you to check with the user first; then call again with completeSubtasks.",
            BinaryData.FromString("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string", "description": "Name of the task to complete." },
                    "completeSubtasks": { "type": "boolean", "description": "Whether to complete the task's open subtasks." },
                    "project": { "type": "string", "description": "The project the task belongs to." }
                },
                "required": ["name"],
                "additionalProperties": false
            }
            """)),

        ChatTool.CreateFunctionTool(
            "delete_task",
            "Use this tool to delete an existing task and its subtasks if any. First call only asks for confirmation. After confirmation, call again with confirmed: true.",
            BinaryData.FromString("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string", "description": "Name of the task to delete." },
                    "project": { "type": "string", "description": "The project the task belongs to." },
                    "confirmed": { "type": "boolean", "description": "True only after the user has confirmed the deletion explicitly." }
                },
                "required": ["name"],
                "additionalProperties": false
            }
            """))
    };



    public string Execute(Session session, string toolName, string argsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var args = document.RootElement;
            if (args.ValueKind != JsonValueKind.Object)
            {
                return Error("The tool arguments must be a JSON object.");
            }

            return toolName switch
            {
                "list_tasks" => ListTasks(args),
                "create_task" => CreateTask(args),
                "update_task" => UpdateTask(args),
                "complete_task" => CompleteTask(args),
                "delete_task" => DeleteTask(session, args),
                _ => Error($"Unknown tool: {toolName}")
            };
        }
        catch (JsonException)
        {
            return Error("The tool arguments must be valid JSON.");
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
    }

    private string ListTasks(JsonElement args)
    {
        var project = OptionalString(args, "project");
        var priority = OptionalPriority(args);
        var status = OptionalString(args, "status") ?? "open";
        var dueWithin = OptionalString(args, "due_within");

        if (status is not ("open" or "done" or "all"))
        {
            return Error("Status must be 'open', 'done', or 'all'.");
        }
        if (dueWithin is not (null or "today" or "week" or "overdue" or "later"))
        {
            return Error("Due within must be 'today', 'week', 'overdue', 'later', or omitted.");
        }

        var today = Today();
        var all = _store.All();
        var names = all.ToDictionary(t => t.Id, t => t.Name);

        var matches = all.Where(t =>
            (status == "all" || (status == "done") == t.Completed) &&
            (project == null || t.Project.Equals(project, StringComparison.OrdinalIgnoreCase)) &&
            (priority == null || t.Priority == priority) &&
            MatchesDue(t, dueWithin, today));

        var tasks = matches.Select(t => new
        {
            t.Name,
            t.Completed,
            t.Project,
            t.Priority,
            DueDate = t.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Parent = t.ParentId != null && names.TryGetValue(t.ParentId.Value, out var parentName) ? parentName : null
        }).ToList();
        return Json(new { count = tasks.Count, tasks });
    }

    private static bool MatchesDue(TaskItem task, string? dueWithin, DateOnly today)
    {
        if (dueWithin == null) return true;
        if (task.DueDate == null) return false;

        var due = task.DueDate.Value;
        return dueWithin switch
        {
            "today" => due == today,
            "week" => due >= today && due <= today.AddDays(7),
            "overdue" => due < today,
            "later" => due > today.AddDays(7),
            _ => true
        };
    }

    private string CreateTask(JsonElement args)
    {
        var name = RequiredString(args, "name");
        var priority = OptionalPriority(args);
        var dueDate = OptionalDate(args, "dueDate");
        var parentName = OptionalString(args, "parent");

        int? parentId = null;
        if (parentName != null)
        {
            var lookup = Resolve(parentName, t => t.ParentId == null);
            if (lookup.Error != null) return lookup.Error;
            parentId = lookup.Task!.Id;
        }
        var task = _store.Create(name, OptionalString(args, "project"), priority, dueDate, parentId);
        return Json(new
        {
            created = Describe(task),
            parent = parentName == null ? null : _store.Get(parentId!.Value)?.Name
        });
    }

    private string UpdateTask(JsonElement args)
    {
        var lookup = Resolve(RequiredString(args, "name"));
        if (lookup.Error != null) return lookup.Error;

        var newName = OptionalString(args, "newName");
        var priority = OptionalPriority(args);
        var dueText = OptionalString(args, "dueDate");
        var clearDueDate = dueText != null && dueText.Equals("none", StringComparison.OrdinalIgnoreCase);
        var dueDate = clearDueDate ? null : OptionalDate(args, "dueDate");

        var task = _store.Update(lookup.Task!.Id, newName, OptionalString(args, "project"), priority, dueDate, clearDueDate);
        return Json(new { updated = Describe(task) });
    }

    private string CompleteTask(JsonElement args)
    {
        var name = RequiredString(args, "name");
        var lookup = Resolve(name, t => !t.Completed);
        if (lookup.Error != null)
        {
            var anyMatch = Resolve(name);
            if (anyMatch.Task is { Completed: true })
            {
                return Json(new { message = $"'{anyMatch.Task.Name}' is already done." });
            }
            return Error(lookup.Error);
        }
        var task = lookup.Task!;
        var openSubtasks = _store.Subtasks(task.Id).Count(t => !t.Completed);
        var completeSubtasks = OptionalBool(args, "completeSubtasks");

        if (openSubtasks > 0 && completeSubtasks == null)
        {
            return Json(new
            {
                needsConfirmation = true,
                message = $"'{task.Name}' has {openSubtasks} open subtask(s). Ask the user whether to complete them too, then call again with completeSubtasks true or false."
            });
        }
        var done = _store.Complete(task.Id, completeSubtasks == true);
        return Json(new { completed = Describe(done), subtasksCompleted = completeSubtasks == true ? openSubtasks : 0 });
    }

    private string DeleteTask(Session session, JsonElement args)
    {
        var lookup = Resolve(RequiredString(args, "name"));
        if (lookup.Error != null) return lookup.Error;
        var task = lookup.Task!;
        var confirmed = OptionalBool(args, "confirmed") == true;
        var askedEarlier = session.PendingDeletes.TryGetValue(task.Id, out var askedInTurn)
            && askedInTurn < session.Turn;
        if (!confirmed && !askedEarlier)
        {
            session.PendingDeletes[task.Id] = session.Turn;
            var subtasks = _store.Subtasks(task.Id).Count;
            var extra = subtasks > 0 ? $" and its {subtasks} subtask(s)" : "";
            return Json(new
            {
                needConfirmation = true,
                message = $"Nothing was deleted. Ask the user to confirm deleting '{task.Name}'{extra}. If they agree in their next message, call delete_task again with confirmed true."
            });
        }
        session.PendingDeletes.Remove(task.Id);
        var removed = _store.Delete(task.Id);
        return Json(new { deleted = task.Name, tasksRemoved = removed });
    }


    private static string Error(string message) => Json(new { error = message });
    private static string Json(object obj) => JsonSerializer.Serialize(obj);
    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
    private record Lookup(TaskItem? Task, string? Error);
    private Lookup Resolve(string name, Func<TaskItem, bool>? filter = null)
    {
        var matches = _store.FindByName(name, filter);
        if (matches.Count == 1) return new Lookup(matches[0], null);
        if (matches.Count == 0) return new Lookup(null, Error($"No task matches '{name}'."));
        var candidates = matches.Select(t => new { t.Name, t.Project }).ToList();
        return new Lookup(null, Json(new
        {
            error = $"More than one task matches '{name}'. Ask the user which one they mean.",
            candidates
        }));
    }
    private static object Describe(TaskItem task) => new
    {
        task.Name,
        task.Completed,
        task.Project,
        task.Priority,
        DueDate = task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };
    private static string RequiredString(JsonElement args, string key)
    {
        return OptionalString(args, key) ?? throw new ArgumentException($"{key} is required");
    }
    private static string? OptionalString(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"{key} must be a string");
        }
        var text = value.GetString()!.Trim();
        return text.Length == 0 ? null : text;
    }
    private static bool? OptionalBool(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"{key} must be a boolean")
        };
    }
    private static Priority? OptionalPriority(JsonElement args)
    {
        var text = OptionalString(args, "priority");
        return text?.ToLowerInvariant() switch
        {
            "low" => Priority.Low,
            "medium" => Priority.Medium,
            "high" => Priority.High,
            null => null,
            _ => throw new ArgumentException("priority must be 'low', 'medium', or 'high'")
        };
    }
    private static DateOnly? OptionalDate(JsonElement args, string key)
    {
        var text = OptionalString(args, key);
        if (text == null) return null;
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new ArgumentException($"{key} must be a valid date in the format 'yyyy-MM-dd'");
        }
        return date;
    }
}
