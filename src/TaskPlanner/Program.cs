using System.Net.Mail;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskPlanner;

var builder = WebApplication.CreateBuilder(args);
var data = Path.GetFullPath(builder.Configuration["DataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
Directory.CreateDirectory(data);
var keyPath = Path.Combine(data, "jwt.key");
var configuredKey = builder.Configuration["Authentication:Key"];
if (configuredKey is null && !File.Exists(keyPath))
    await File.WriteAllBytesAsync(keyPath, RandomNumberGenerator.GetBytes(64));
var key = configuredKey is null ? await File.ReadAllBytesAsync(keyPath)
    : Convert.FromBase64String(configuredKey);
if (key.Length < 32) throw new InvalidOperationException("JWT key must contain at least 32 bytes");
var tokens = new TokenService(key, builder.Configuration.GetValue("Authentication:LifetimeSeconds", 900));
builder.Services.AddSingleton(tokens);
builder.Services.AddDbContext<PlannerDb>(o => o.UseSqlite(
    new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "planner.db"),
        ForeignKeys = true, DefaultTimeout = 30 }.ToString()));
builder.Services.AddSingleton<IPasswordHasher<User>>(
    new PasswordHasher<User>(Microsoft.Extensions.Options.Options.Create(
        new PasswordHasherOptions { IterationCount = 210_000 })));
builder.Services.AddScoped<INotifier, DatabaseNotifier>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddHostedService<ReminderWorker>();
builder.Services.AddAuthentication("Bearer").AddJwtBearer("Bearer", o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = tokens.Validation;
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlannerDb>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/auth/register", async (Credentials input, PlannerDb db,
    IPasswordHasher<User> hasher) =>
{
    var email = input.Email?.Trim().ToLowerInvariant() ?? "";
    if (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || email.Length > 254)
        return Error("Укажите корректный email");
    if (input.Password is null || input.Password.Length is < 10 or > 128)
        return Error("Пароль должен содержать от 10 до 128 символов");
    var user = new User { Email = email };
    user.PasswordHash = hasher.HashPassword(user, input.Password);
    db.Users.Add(user);
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteErrorCode: 19 })
    { return Results.Conflict(new { error = "Такой email уже зарегистрирован" }); }
    return Results.Created("/api/auth/login", new { user.Id, user.Email });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (Credentials input, PlannerDb db,
    IPasswordHasher<User> hasher, HttpContext ctx) =>
{
    var email = input.Email?.Trim().ToLowerInvariant() ?? "";
    var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
    if (user is null || input.Password is null || input.Password.Length > 128
        || hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password)
            == PasswordVerificationResult.Failed)
        return Results.Json(new { error = "Неверный email или пароль" }, statusCode: 401);
    var refresh = TokenService.NewRefresh();
    db.Sessions.Add(new RefreshSession { Hash = TokenService.Hash(refresh), UserId = user.Id,
        ExpiresAt = DateTime.UtcNow.AddDays(7) });
    await db.SaveChangesAsync();
    TokenService.Cookie(ctx, refresh);
    return Results.Ok(tokens.Issue(user));
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/refresh", async (HttpContext ctx, PlannerDb db) =>
{
    if (!TokenService.SameOrigin(ctx)) return Results.StatusCode(403);
    var raw = ctx.Request.Cookies["planner_refresh"];
    if (raw is null) return Results.Unauthorized();
    var hash = TokenService.Hash(raw);
    await using var tx = await db.Database.BeginTransactionAsync();
    var old = await db.Sessions.SingleOrDefaultAsync(s => s.Hash == hash && s.ExpiresAt > DateTime.UtcNow);
    if (old is null) return Results.Unauthorized();
    if (await db.Sessions.Where(s => s.Hash == hash).ExecuteDeleteAsync() != 1)
        return Results.Unauthorized();
    var user = await db.Users.FindAsync(old.UserId);
    if (user is null) return Results.Unauthorized();
    var next = TokenService.NewRefresh();
    db.Sessions.Add(new RefreshSession { Hash = TokenService.Hash(next), UserId = user.Id,
        ExpiresAt = DateTime.UtcNow.AddDays(7) });
    await db.SaveChangesAsync(); await tx.CommitAsync();
    TokenService.Cookie(ctx, next);
    return Results.Ok(tokens.Issue(user));
});

app.MapPost("/api/auth/logout", async (HttpContext ctx, PlannerDb db) =>
{
    if (!TokenService.SameOrigin(ctx)) return Results.StatusCode(403);
    var raw = ctx.Request.Cookies["planner_refresh"];
    if (raw is not null)
    {
        var hash = TokenService.Hash(raw);
        await db.Sessions.Where(s => s.Hash == hash).ExecuteDeleteAsync();
    }
    ctx.Response.Cookies.Delete("planner_refresh", new CookieOptions { Path = "/api/auth" });
    return Results.NoContent();
});

var tasks = app.MapGroup("/api/tasks").RequireAuthorization();
tasks.MapGet("", async (HttpContext ctx, PlannerDb db, string? state, string? sort, string? search) =>
{
    var uid = TokenService.UserId(ctx.User);
    if (state is not (null or "all" or "active" or "completed")) return Error("Неизвестный фильтр");
    if (sort is not (null or "due" or "created")) return Error("Неизвестная сортировка");
    var q = db.Tasks.AsNoTracking().Include(t => t.Owner).Include(t => t.Shares)
        .Where(t => t.OwnerId == uid || t.Shares.Any(s => s.UserId == uid));
    if (state == "active") q = q.Where(t => !t.Completed);
    if (state == "completed") q = q.Where(t => t.Completed);
    if (!string.IsNullOrWhiteSpace(search)) q = q.Where(t => t.Title.Contains(search));
    q = sort == "created" ? q.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
        : q.OrderBy(t => t.DueAt).ThenBy(t => t.Id);
    var result = await q.ToListAsync();
    return Results.Ok(result.Select(t => View(t, uid)));
});
tasks.MapPost("", async (TaskInput input, HttpContext ctx, PlannerDb db) =>
{
    var error = Validate(input); if (error is not null) return Error(error);
    var task = new PlannerTask { OwnerId = TokenService.UserId(ctx.User),
        Title = input.Title.Trim(), Description = input.Description?.Trim(),
        DueAt = input.DueAt.UtcDateTime, CreatedAt = DateTime.UtcNow };
    db.Tasks.Add(task); await db.SaveChangesAsync();
    return Results.Created($"/api/tasks/{task.Id}", new { task.Id });
});
tasks.MapPut("/{id:int}", async (int id, TaskInput input, HttpContext ctx, PlannerDb db) =>
{
    var task = await Find(db, id); if (task is null) return Results.NotFound();
    if (!CanEdit(task, TokenService.UserId(ctx.User))) return Results.StatusCode(403);
    var error = Validate(input); if (error is not null) return Error(error);
    task.Title = input.Title.Trim(); task.Description = input.Description?.Trim();
    task.DueAt = input.DueAt.UtcDateTime; await db.SaveChangesAsync();
    return Results.NoContent();
});
tasks.MapPatch("/{id:int}/completion", async (int id, CompletionInput input, HttpContext ctx, PlannerDb db) =>
{
    var task = await Find(db, id); if (task is null) return Results.NotFound();
    if (!CanEdit(task, TokenService.UserId(ctx.User))) return Results.StatusCode(403);
    task.Completed = input.Completed; await db.SaveChangesAsync(); return Results.NoContent();
});
tasks.MapDelete("/{id:int}", async (int id, HttpContext ctx, PlannerDb db) =>
{
    var task = await db.Tasks.FindAsync(id); if (task is null) return Results.NotFound();
    if (task.OwnerId != TokenService.UserId(ctx.User)) return Results.StatusCode(403);
    db.Tasks.Remove(task); await db.SaveChangesAsync(); return Results.NoContent();
});
tasks.MapGet("/{id:int}/shares", async (int id, HttpContext ctx, PlannerDb db) =>
{
    var task = await db.Tasks.FindAsync(id); if (task is null) return Results.NotFound();
    if (task.OwnerId != TokenService.UserId(ctx.User)) return Results.StatusCode(403);
    return Results.Ok(await db.Shares.Where(s => s.TaskId == id)
        .Select(s => new { s.UserId, s.User.Email, s.CanEdit }).ToListAsync());
});
tasks.MapPost("/{id:int}/shares", async (int id, ShareInput input, HttpContext ctx, PlannerDb db) =>
{
    var task = await db.Tasks.FindAsync(id); if (task is null) return Results.NotFound();
    if (task.OwnerId != TokenService.UserId(ctx.User)) return Results.StatusCode(403);
    var email = input.Email?.Trim().ToLowerInvariant() ?? "";
    var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
    if (user is null) return Error("Пользователь должен сначала зарегистрироваться");
    if (user.Id == task.OwnerId) return Error("Вы уже владелец задачи");
    var share = await db.Shares.FindAsync(id, user.Id);
    if (share is null) db.Shares.Add(new TaskShare { TaskId = id, UserId = user.Id, CanEdit = input.CanEdit });
    else share.CanEdit = input.CanEdit;
    await db.SaveChangesAsync(); return Results.NoContent();
});
tasks.MapDelete("/{id:int}/shares/{userId:int}", async (int id, int userId, HttpContext ctx, PlannerDb db) =>
{
    var task = await db.Tasks.FindAsync(id); if (task is null) return Results.NotFound();
    if (task.OwnerId != TokenService.UserId(ctx.User)) return Results.StatusCode(403);
    await db.Shares.Where(s => s.TaskId == id && s.UserId == userId).ExecuteDeleteAsync();
    return Results.NoContent();
});
var notifications = app.MapGroup("/api/notifications").RequireAuthorization();
notifications.MapGet("", async (HttpContext ctx, PlannerDb db) =>
    Results.Ok(await db.Reminders.AsNoTracking().Where(r => r.UserId == TokenService.UserId(ctx.User))
        .OrderByDescending(r => r.Id).Take(50)
        .Select(r => new { r.Id, r.TaskId, r.Message, r.IsRead, r.CreatedAt }).ToListAsync()));
notifications.MapPatch("/{id:int}/read", async (int id, HttpContext ctx, PlannerDb db) =>
{
    var item = await db.Reminders.FindAsync(id); if (item is null) return Results.NotFound();
    if (item.UserId != TokenService.UserId(ctx.User)) return Results.StatusCode(403);
    item.IsRead = true; await db.SaveChangesAsync(); return Results.NoContent();
});
app.Run();

static IResult Error(string error) => Results.BadRequest(new { error });
static string? Validate(TaskInput input) => string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200
    ? "Название обязательно, не более 200 символов"
    : input.Description?.Length > 4000 ? "Описание: не более 4000 символов"
    : input.DueAt.Year < 2000 || input.DueAt.Year > 2100 ? "Укажите допустимый срок" : null;
static Task<PlannerTask?> Find(PlannerDb db, int id) => db.Tasks.Include(t => t.Owner)
    .Include(t => t.Shares).SingleOrDefaultAsync(t => t.Id == id);
static bool CanEdit(PlannerTask task, int uid) => task.OwnerId == uid
    || task.Shares.Any(s => s.UserId == uid && s.CanEdit);
static object View(PlannerTask task, int uid) => new { task.Id, task.Title, task.Description,
    dueAt = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc),
    createdAt = DateTime.SpecifyKind(task.CreatedAt, DateTimeKind.Utc), task.Completed,
    isOwner = task.OwnerId == uid, canEdit = CanEdit(task, uid), ownerEmail = task.Owner.Email };
