using Olympiad.Materials.Api;
using Olympiad.Workflows.Infrastructure;
using Olympiad.Workflows.Application;

var builder = WebApplication.CreateBuilder(args);
await CompositionRoot.ConfigureAsync(builder, CancellationToken.None);
builder.AddMaterialWorkflows();
var app = builder.Build();
app.UseRouting();
app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<IdentityMiddleware>();
if(app.Services.GetService<IProcessStore>() is not null) await app.UseWorkflowsAsync();
MaterialEndpoints.Map(app);
await app.RunAsync();

public partial class Program;
