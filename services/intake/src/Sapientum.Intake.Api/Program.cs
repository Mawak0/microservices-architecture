using Sapientum.Intake.Api;
using Sapientum.Intake.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.AddIntake();
var app = builder.Build();
await app.Services.GetRequiredService<SqliteDatabase>().InitializeAsync();
app.UseRouting();
app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<AuthorizationMiddleware>();
app.MapIntake();
await app.RunAsync();

// Exposes the entry point to in-process HTTP integration tests without changing runtime behavior.
public partial class Program { }
