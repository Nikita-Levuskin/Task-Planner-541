using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TaskPlanner;

public interface INotifier
{
    Task NotifyAsync(PlannerTask task, DateTime now, CancellationToken ct);
}

public sealed class DatabaseNotifier(PlannerDb db) : INotifier
{
    public async Task NotifyAsync(PlannerTask task, DateTime now, CancellationToken ct)
    {
        // The durable notification is the delivery. Its unique key prevents duplicates,
        // including concurrent workers. Browser popups are an optional view of this inbox.
        var reminder = new Reminder { TaskId = task.Id, UserId = task.OwnerId,
            DueAt = task.DueAt, CreatedAt = now,
            Message = (task.DueAt <= now ? "Срок наступил: " : "Скоро срок: ") + task.Title };
        db.Reminders.Add(reminder);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            db.Entry(reminder).State = EntityState.Detached;
        }
    }
}

public sealed class ReminderService(PlannerDb db, INotifier notifier)
{
    public async Task CheckAsync(DateTime now, CancellationToken ct = default)
    {
        var border = now.AddMinutes(30);
        var due = await db.Tasks.Where(t => !t.Completed && t.DueAt <= border
            && !db.Reminders.Any(r => r.TaskId == t.Id && r.UserId == t.OwnerId && r.DueAt == t.DueAt))
            .ToListAsync(ct);
        foreach (var task in due) await notifier.NotifyAsync(task, now, ct);
    }
}

public sealed class ReminderWorker(IServiceScopeFactory scopes, ILogger<ReminderWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReminderService>()
                    .CheckAsync(DateTime.UtcNow, stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogError(e, "Reminder check failed; the next interval will retry");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
