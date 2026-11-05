using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TaskPlanner;

int count = 0;
void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
async Task Check(string name, Func<Task> test) { await test(); count++; Console.WriteLine($"PASS {name}"); }
var key = RandomNumberGenerator.GetBytes(64);
var tokenService = new TokenService(key, 900);
string Token(TokenService service) => JsonSerializer.SerializeToElement(service.Issue(new User { Id = 4, Email = "test@example.com" })).GetProperty("accessToken").GetString()!;
await Check("signed token and user claim", () => { var h = new JwtSecurityTokenHandler { MapInboundClaims = false }; var p = h.ValidateToken(Token(tokenService), tokenService.Validation, out _); Assert(p.FindFirst("sub")?.Value == "4"); return Task.CompletedTask; });
await Check("wrong signing key rejected", () => { try { new JwtSecurityTokenHandler().ValidateToken(Token(tokenService), new TokenService(RandomNumberGenerator.GetBytes(64), 900).Validation, out _); throw new Exception("Expected rejection"); } catch (SecurityTokenException) { } return Task.CompletedTask; });
await Check("expired token rejected", () => { try { new JwtSecurityTokenHandler().ValidateToken(Token(new TokenService(key, -10)), tokenService.Validation, out _); throw new Exception("Expected expiry"); } catch (SecurityTokenExpiredException) { } return Task.CompletedTask; });
await Check("refresh token is random and only hash is stored", () => { var a = TokenService.NewRefresh(); var b = TokenService.NewRefresh(); Assert(a != b && a.Length == 64 && TokenService.Hash(a) != a); return Task.CompletedTask; });
await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
var options = new DbContextOptionsBuilder<PlannerDb>().UseSqlite(connection).Options;
await using var db = new PlannerDb(options); await db.Database.EnsureCreatedAsync();
var owner = new User { Email = "owner@example.com", PasswordHash = "test-only" }; db.Users.Add(owner); await db.SaveChangesAsync();
var now = DateTime.UtcNow;
var task = new PlannerTask { OwnerId = owner.Id, Title = "Reminder", DueAt = now.AddMinutes(20), CreatedAt = now };
db.Tasks.Add(task); await db.SaveChangesAsync();
var reminders = new ReminderService(db, new DatabaseNotifier(db));
await Check("near deadline creates durable notification", async () => { await reminders.CheckAsync(now); Assert(await db.Reminders.CountAsync() == 1); });
await Check("repeated check is idempotent", async () => { await reminders.CheckAsync(now); await reminders.CheckAsync(now); Assert(await db.Reminders.CountAsync() == 1); });
await Check("changed deadline receives a new reminder", async () => { task.DueAt = now.AddMinutes(10); await db.SaveChangesAsync(); await reminders.CheckAsync(now); Assert(await db.Reminders.CountAsync() == 2); });
await Check("completed task gets no reminder", async () => { task.Completed = true; task.DueAt = now.AddMinutes(5); await db.SaveChangesAsync(); await reminders.CheckAsync(now); Assert(await db.Reminders.CountAsync() == 2); });
await Check("distant task gets no early reminder", async () => { task.Completed = false; task.DueAt = now.AddHours(2); await db.SaveChangesAsync(); await reminders.CheckAsync(now); Assert(await db.Reminders.CountAsync() == 2); });
await Check("overdue task after restart is not lost", async () => { task.DueAt = now.AddMinutes(-3); await db.SaveChangesAsync(); await reminders.CheckAsync(now); Assert(await db.Reminders.CountAsync() == 3); });
await Check("database unique constraint prevents duplicate delivery", async () => { await new DatabaseNotifier(db).NotifyAsync(task, now, default); Assert(await db.Reminders.CountAsync() == 3); });
await Check("task removal cleans dependent reminders", async () => { db.Tasks.Remove(task); await db.SaveChangesAsync(); Assert(await db.Reminders.CountAsync() == 0); });
Console.WriteLine($"{count} tests passed");
