using Microsoft.EntityFrameworkCore;

namespace TaskPlanner;

public sealed class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public sealed class PlannerTask
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool Completed { get; set; }
    public List<TaskShare> Shares { get; set; } = [];
}

public sealed class TaskShare
{
    public int TaskId { get; set; }
    public PlannerTask Task { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public bool CanEdit { get; set; }
}

public sealed class RefreshSession
{
    public string Hash { get; set; } = "";
    public int UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public sealed class Reminder
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public PlannerTask Task { get; set; } = null!;
    public int UserId { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Message { get; set; } = "";
    public bool IsRead { get; set; }
}

public sealed class PlannerDb(DbContextOptions<PlannerDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<PlannerTask> Tasks => Set<PlannerTask>();
    public DbSet<TaskShare> Shares => Set<TaskShare>();
    public DbSet<RefreshSession> Sessions => Set<RefreshSession>();
    public DbSet<Reminder> Reminders => Set<Reminder>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(x => x.Email).IsUnique();
        b.Entity<User>().Property(x => x.Email).HasMaxLength(254);
        b.Entity<PlannerTask>().Property(x => x.Title).HasMaxLength(200);
        b.Entity<PlannerTask>().Property(x => x.Description).HasMaxLength(4000);
        b.Entity<TaskShare>().HasKey(x => new { x.TaskId, x.UserId });
        b.Entity<TaskShare>().HasOne(x => x.Task).WithMany(x => x.Shares)
            .HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RefreshSession>().HasKey(x => x.Hash);
        b.Entity<Reminder>().HasIndex(x => new { x.TaskId, x.UserId, x.DueAt }).IsUnique();
        b.Entity<Reminder>().HasOne(x => x.Task).WithMany()
            .HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed record Credentials(string Email, string Password);
public sealed record TaskInput(string Title, string? Description, DateTimeOffset DueAt);
public sealed record CompletionInput(bool Completed);
public sealed record ShareInput(string Email, bool CanEdit);
