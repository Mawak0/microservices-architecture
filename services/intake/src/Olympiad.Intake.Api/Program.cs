using Olympiad.Intake.Api;
using Olympiad.Intake.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.AddIntake();
var app = builder.Build();
await app.Services.GetRequiredService<PostgresDatabase>().InitializeAsync();
if (app.Services.GetService<Olympiad.Storage.S3.S3Objects>() is { } objects) await objects.InitializeAsync(default);
app.UseRouting();
app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<AuthorizationMiddleware>();
app.MapIntake();
await app.RunAsync();

// Exposes the entry point to in-process HTTP integration tests without changing runtime behavior.
public partial class Program { }
