namespace ProjectManagerAgent.Models;

public enum Priority
{
    Low,
    Medium,
    High
}

public class TaskItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Completed { get; set; }
    public string Project { get; set; } = "Inbox";
    public Priority Priority { get; set; } = Priority.Medium;
    public DateOnly? DueDate { get; set; }
    public int? ParentId { get; set; }
    public DateTime CreatedAt { get; set; }

    public TaskItem Copy() => (TaskItem)MemberwiseClone();
}