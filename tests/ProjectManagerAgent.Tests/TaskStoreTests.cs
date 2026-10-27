using System.Threading.Channels;
using ProjectManagerAgent.Models;
using ProjectManagerAgent.Services;

namespace ProjectManagerAgent.Tests;

public class TaskStoreTests
{
    private static TaskStore NewStore() => new(null);

    [Fact]
    public void Create_gives_incrementing_ids_and_defaults()
    {
        var store = NewStore();
        var task1 = store.Create("Visit Barber", null, null, null, null);
        var task2 = store.Create("Call Mom", null, null, null, null);

        Assert.Equal(1, task1.Id);
        Assert.Equal(2, task2.Id);
        Assert.Equal(TaskStore.DefaultProject, task1.Project);
        Assert.Equal(TaskStore.DefaultProject, task2.Project);
        Assert.Equal(Priority.Medium, task1.Priority);
        Assert.Equal(Priority.Medium, task2.Priority);
        Assert.False(task1.Completed);
        Assert.False(task2.Completed);
    }

    [Fact]
    public void Create_rejects_an_empty_name()
    {
        Assert.Throws<ArgumentException>(() => NewStore().Create("   ", null, null, null, null));
    }

    [Fact]
    public void Subtasks_inherits_the_project_of_its_parent()
    {
        var store = NewStore();
        var parent = store.Create("Report", "Work", Priority.High, null, null);
        var child = store.Create("Draft", null, null, null, parent.Id);

        Assert.Equal("Work", child.Project);
        Assert.Equal(Priority.High, child.Priority);
        Assert.Equal(parent.Id, child.ParentId);
    }

    [Fact]
    public void Subtasks_cannot_be_nested_further()
    {
        var store = NewStore();
        var parent = store.Create("Report", null, null, null, null);
        var child = store.Create("Draft", null, null, null, parent.Id);

        Assert.Throws<ArgumentException>(() =>
            store.Create("Grandchild Task", null, null, null, child.Id)
        );
    }

    [Fact]
    public void Create_rejects_missing_parent()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() =>
            store.Create("Orphan Task", null, null, null, 999)
        );
    }

    [Fact]
    public void FindByName_prefers_exact_matches_and_ignore_case()
    {
        var store = NewStore();
        store.Create("Buy Groceries", null, null, null, null);
        store.Create("Buy Groceries For The Week", null, null, null, null);

        var exact = store.FindByName("Buy Groceries");
        var partial = store.FindByName("Week");

        Assert.Single(exact);
        Assert.Equal("Buy Groceries", exact[0].Name);
        Assert.Single(partial);
    }

    [Fact]
    public void FindByName_returns_every_match_when_it_is_ambiguous()
    {
        var store = NewStore();
        store.Create("Email TA", null, null, null, null);
        store.Create("Email Mattias", null, null, null, null);

        Assert.Equal(2, store.FindByName("email").Count);
    }

    [Fact]
    public void Complete_can_include_subtasks()
    {
        var store = NewStore();
        var parent = store.Create("Report", null, null, null, null);
        var child = store.Create("Draft", null, null, null, parent.Id);

        store.Complete(parent.Id, includeSubtasks: true);

        Assert.True(store.Get(parent.Id)!.Completed);
        Assert.True(store.Get(child.Id)!.Completed);
    }

    [Fact]
    public void Complete_leaves_subtasks_alone_by_default()
    {
        var store = NewStore();
        var parent = store.Create("Report", null, null, null, null);
        var child = store.Create("Draft", null, null, null, parent.Id);

        store.Complete(parent.Id, includeSubtasks: false);

        Assert.False(store.Get(child.Id)!.Completed);
    }

    [Fact]
    public void Remove_deletes_the_task_and_its_subtasks()
    {
        var store = NewStore();
        var parent = store.Create("Report", null, null, null, null);
        store.Create("Draft", null, null, null, parent.Id);
        store.Create("Others", null, null, null, null);

        var removed = store.Delete(parent.Id);

        Assert.Equal(2, removed);
        Assert.Single(store.All());
    }

    [Fact]
    public void Changing_the_project_of_a_parent_moves_its_subtasks()
    {
        var store = NewStore();
        var parent = store.Create("Report", "Work", null, null, null);
        var child = store.Create("Draft", null, null, null, parent.Id);

        store.Update(parent.Id, null, "Home", null, null, false);

        Assert.Equal("Home", store.Get(parent.Id)!.Project);
    }

    [Fact]
    public void Update_can_clear_a_due_date()
    {
        var store = NewStore();
        var task = store.Create("Report", null, null, new DateOnly(2026, 10, 27), null);
        var updated = store.Update(task.Id, null, null, null, null, clearDueDate: true);

        Assert.Null(updated.DueDate);
    }

    [Fact]
    public void Tasks_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

        try
        {
            var before = new TaskStore(path);
            var parent = before.Create("Report", "Work", Priority.High, new DateOnly(2026, 10, 27), null);
            before.Create("Draft", null, null, null, parent.Id);

            var after = new TaskStore(path);
            var tasks = after.All();

            Assert.Equal(2, tasks.Count);
            Assert.Equal(new DateOnly(2026, 10, 27), tasks[0].DueDate);
            Assert.Equal(Priority.High, tasks[0].Priority);

            Assert.Equal(3, after.Create("Next", null, null, null, null).Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_file_uses_readable_camelCase_names_and_loads_by_hand_edit()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """[{"id":7, "name":"By hand", "Completed":false, "project":"Work", "priority":"high", "dueDate":"2026-10-27", "parentId":null, "createdAt":"2026-10-26T08:00:30Z"}]""");

        try
        {
            var store = new TaskStore(path);
            var task = store.All().Single();
            Assert.Equal(7, task.Id);
            Assert.Equal("By hand", task.Name);
            Assert.Equal(Priority.High, task.Priority);

            store.Create("Another", null, null, null, null);
            var saved = File.ReadAllText(path);
            Assert.Contains("\"priority\": \"high\"", saved);
            Assert.Contains("\"dueDate\": \"2026-10-27\"", saved);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_corrupted_file_is_reported_and_not_overwritten()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{ not json");

        try
        {
            Assert.Throws<InvalidOperationException>(() => new TaskStore(path));
            Assert.Equal("{ not json", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}