using Olympiad.Materials.Api;

var builder = WebApplication.CreateBuilder(args);
await CompositionRoot.ConfigureAsync(builder, CancellationToken.None);
var app = builder.Build();
app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<IdentityMiddleware>();
MaterialEndpoints.Map(app);
await app.RunAsync();

public partial class Program;
