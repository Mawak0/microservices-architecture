using Olympiad.Intake.Api;
using Olympiad.Intake.Infrastructure.Persistence;
using Olympiad.Workflows.Infrastructure;
using Olympiad.Workflows.Application;

var builder = WebApplication.CreateBuilder(args);
var storage=builder.AddIntake();
builder.AddIntakeWorkflows(storage.Database,storage.Objects);
var app = builder.Build();
await app.Services.GetRequiredService<PostgresDatabase>().InitializeAsync();
if (app.Services.GetService<Olympiad.Storage.S3.S3Objects>() is { } objects) await objects.InitializeAsync(default);
app.UseRouting();
app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<AuthorizationMiddleware>();
if(app.Services.GetService<IProcessStore>() is not null) await app.UseWorkflowsAsync();
app.MapIntake();
await app.RunAsync();

// Exposes the entry point to in-process HTTP integration tests without changing runtime behavior.
public partial class Program { }
