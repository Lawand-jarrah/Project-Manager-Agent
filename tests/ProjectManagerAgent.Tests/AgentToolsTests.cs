using System.Text.Json;
using ProjectManagerAgent.Agent;
using ProjectManagerAgent.Models;
using ProjectManagerAgent.Services;

namespace ProjectManagerAgent.Tests;

public class FixedTime : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

public class AgentToolsTests
{
    private readonly TaskStore _store = new(null, new FixedTime());
    private readonly AgentTools _tools;
    private readonly Session _session = new("test");

    public AgentToolsTests()
    {
        _tools = new AgentTools(_store, new FixedTime());
    }

    private JsonElement Run(string tool, string args)
    {
        return JsonDocument.Parse(_tools.Execute(_session, tool, args)).RootElement.Clone();
    }

    private static string? ErrorOf(JsonElement result)
    {
        return result.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    [Fact]
    public void List_filters_by_due_date_range()
    {
        _store.Create("Today", null, null, new DateOnly(2026, 10, 31), null);
        _store.Create("Friday", null, null, new DateOnly(2026, 11, 06), null);
        _store.Create("Late", null, null, new DateOnly(2026, 10, 30), null);
        _store.Create("Far Away", null, null, new DateOnly(2026, 11, 30), null);
        _store.Create("No date", null, null, null, null);

        Assert.Equal(2, Run("list_tasks", """{"dueWithin":"week"}""").GetProperty("count").GetInt32());
        Assert.Equal(1, Run("list_tasks", """{"dueWithin":"today"}""").GetProperty("count").GetInt32());
        Assert.Equal(1, Run("list_tasks", """{"dueWithin":"overdue"}""").GetProperty("count").GetInt32());
        Assert.Equal(1, Run("list_tasks", """{"dueWithin":"later"}""").GetProperty("count").GetInt32());
    }

    [Fact]
    public void List_filters_by_project_and_priority()
    {
        _store.Create("Task 1", "Work", Priority.High, null, null);
        _store.Create("Task 2", "Work", Priority.Low, null, null);
        _store.Create("Task 3", "Personal", Priority.Medium, null, null);

        var result = Run("list_tasks", """{"project":"Work", "priority":"high"}""");
        Assert.Equal(1, result.GetProperty("count").GetInt32());
    }

    [Fact]
    public void List_rejects_an_unknown_status()
    {
        Assert.NotNull(ErrorOf(Run("list_tasks", """{"status":"maybe"}""")));
    }

    [Fact]
    public void Create_makes_a_task_with_all_fields()
    {
        var result = Run("create_task", """{"name":"Report", "project":"Work", "priority":"high", "dueDate":"2026-11-01"}""");
        Assert.Null(ErrorOf(result));
        var task = _store.All().Single();
        Assert.Equal("Work", task.Project);
        Assert.Equal(Priority.High, task.Priority);
        Assert.Equal(new DateOnly(2026, 11, 01), task.DueDate);
    }

    [Fact]
    public void Create_can_nest_a_task_under_a_parent_found_by_name()
    {
        _store.Create("Quarterly Report", "Work", null, null, null);
        Run("create_task", """{"name":"Send to legal", "parent":"quarterly"}""");
        var child = _store.All().Single(t => t.Name == "Send to legal");
        Assert.Equal(1, child.ParentId);
        Assert.Equal("Work", child.Project);
    }

    [Fact]
    public void Create_reports_an_unknown_parent()
    {
        var result = Run("create_task", """{"name":"X", "parent":"Nothing"}""");
        Assert.Contains("No task matches", ErrorOf(result));
        Assert.Empty(_store.All());
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"name":"X", "priority":"Urgent"}""")]
    [InlineData("""{"name":"X", "dueDate":"Next Friday"}""")]
    [InlineData("""{"name":42}""")]
    [InlineData("""Not JSON""")]
    [InlineData("""[1, 2]""")]

    public void Create_returns_an_error_for_bad_input_and_never_throws(string args)
    {
        var result = Run("create_task", args);
        Assert.NotNull(ErrorOf(result));
        Assert.Empty(_store.All());
    }

    [Fact]
    public void An_ambiguous_name_returns_the_candidates()
    {
        _store.Create("Email Mattias", "Work", null, null, null);
        _store.Create("Email Dilan", "Personal", null, null, null);
        var result = Run("complete_task", """{"name":"Email"}""");

        Assert.Contains("More than one", ErrorOf(result));
        Assert.Equal(2, result.GetProperty("candidates").GetArrayLength());
        Assert.All(_store.All(), t => Assert.False(t.Completed));
    }

    [Fact]
    public void Update_Can_rename_and_clear_the_due_date()
    {
        _store.Create("Report", null, null, new DateOnly(2026, 11, 01), null);
        Run("update_task", """{"name":"Report", "newName":"Updated Report", "dueDate":"none"}""");

        var task = _store.All().Single();
        Assert.Equal("Updated Report", task.Name);
        Assert.Null(task.DueDate);
    }

    [Fact]
    public void Completing_a_parent_with_open_subtasks_asks_first()
    {
        var parent = _store.Create("Report", null, null, null, null);
        _store.Create("Draft", null, null, null, parent.Id);
        var result = Run("complete_task", """{"name":"Report"}""");

        Assert.True(result.GetProperty("needsConfirmation").GetBoolean());
        Assert.False(_store.Get(parent.Id)!.Completed);
    }

    [Fact]
    public void Completing_a_parent_can_include_its_subtasks()
    {
        var parent = _store.Create("Report", null, null, null, null);
        var child = _store.Create("Draft", null, null, null, parent.Id);
        Run("complete_task", """{"name":"Report", "completeSubtasks":true}""");

        Assert.True(_store.Get(parent.Id)!.Completed);
        Assert.True(_store.Get(child.Id)!.Completed);
    }

    [Fact]
    public void Completing_a_parent_can_leave_its_subtasks_open()
    {
        var parent = _store.Create("Report", null, null, null, null);
        var child = _store.Create("Draft", null, null, null, parent.Id);
        Run("complete_task", """{"name":"Report", "completeSubtasks":false}""");

        Assert.True(_store.Get(parent.Id)!.Completed);
        Assert.False(_store.Get(child.Id)!.Completed);
    }

    [Fact]
    public void Completing_a_task_that_is_already_done_says_so()
    {
        var task = _store.Create("Report", null, null, null, null);
        _store.Complete(task.Id, false);
        var result = Run("complete_task", """{"name":"Report"}""");
        Assert.Contains("already done", result.GetProperty("message").GetString());
    }

    [Fact]
    public void Delete_never_removes_anything_on_the_first_call()
    {
        _session.Turn = 1;
        _store.Create("Report", null, null, null, null);
        var result = Run("delete_task", """{"name":"Report"}""");

        Assert.True(result.GetProperty("needsConfirmation").GetBoolean());
        Assert.Single(_store.All());
    }

    [Fact]
    public void Delete_refuses_confirmation_true_in_the_same_turn()
    {
        _session.Turn = 1;
        _store.Create("Report", null, null, null, null);
        var result = Run("delete_task", """{"name":"Report", "confirmation":true}""");

        Assert.True(result.GetProperty("needsConfirmation").GetBoolean());
        Assert.Single(_store.All());
    }

    [Fact]
    public void Delete_goes_through_after_the_user_confirms_in_a_later_turn()
    {
        _session.Turn = 1;
        var parent = _store.Create("Report", null, null, null, null);
        _store.Create("Draft", null, null, null, parent.Id);
        Run("delete_task", """{"name":"Report"}""");

        _session.Turn = 2;
        var result = Run("delete_task", """{"name":"Report", "confirmed":true}""");

        Assert.Equal(2, result.GetProperty("tasksRemoved").GetInt32());
        Assert.Empty(_store.All());
    }

    [Fact]
    public void A_confirmation_for_one_task_does_not_delete_another()
    {
        _session.Turn = 1;
        _store.Create("Email Mattias", null, null, null, null);
        _store.Create("Call Dilan", null, null, null, null);
        Run("delete_task", """{"name":"Email Mattias"}""");

        _session.Turn = 2;
        var result = Run("delete_task", """{"name":"Call Dilan", "confirmed":true}""");

        Assert.True(result.GetProperty("needsConfirmation").GetBoolean());
        Assert.Equal(2, _store.All().Count);
    }

    [Fact]
    public void Several_deletes_can_wait_for_one_confirmation()
    {
        _session.Turn = 1;
        _store.Create("Email Mattias", null, null, null, null);
        _store.Create("Call Dilan", null, null, null, null);
        Run("delete_task", """{"name":"Email Mattias"}""");
        Run("delete_task", """{"name":"Call Dilan"}""");

        _session.Turn = 2;
        Run("delete_task", """{"name":"Email Mattias", "confirmed":true}""");
        Run("delete_task", """{"name":"Call Dilan", "confirmed":true}""");

        Assert.Empty(_store.All());
    }
}