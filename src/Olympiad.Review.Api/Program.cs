using Olympiad.Review.Api;
using Olympiad.Review.Application;
using Olympiad.Review.Domain;
using Olympiad.Review.Infrastructure;
using System.Text.Json.Serialization.Metadata;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    var resolver = new DefaultJsonTypeInfoResolver();
    resolver.Modifiers.Add(info =>
    {
        if (info.Type == typeof(HistoryView))
            info.Properties.Single(p => p.Name == "reason").ShouldSerialize = (_, value) => value is not null;
    });
    options.SerializerOptions.TypeInfoResolver = resolver;
});
var configuration = builder.Configuration;
var dependencies = configuration.GetSection("Dependencies");
var defaultUrl = dependencies["BaseUrl"];
var accessToken = dependencies["AccessToken"];
DependencyHttp Dependency(string key)
{
    var value = dependencies[key + "BaseUrl"] ?? defaultUrl;
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Configure Dependencies:" + key + "BaseUrl.");
    var address = new Uri(value.TrimEnd('/') + "/");
    return new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, address, accessToken);
}
var database = new SqliteDatabase(configuration["Storage:ConnectionString"] ?? "Data Source=.data/review.db");
database.Initialize();
builder.Services.AddSingleton(database);
builder.Services.AddSingleton<ITransactions, SqliteTransactions>();
builder.Services.AddSingleton<IGameProfiles>(new HttpGameProfiles(Dependency("Games")));
builder.Services.AddSingleton<IAcceptedReceipts>(new HttpAcceptedReceipts(Dependency("Receipts")));
builder.Services.AddSingleton<ITaskAnswers>(new HttpTaskAnswers(Dependency("Tasks")));
builder.Services.AddSingleton<IIdentityProvider>(new HttpIdentityProvider(Dependency("Identity")));
builder.Services.AddSingleton<IEventPublisher>(new HttpEventPublisher(Dependency("Events")));
if (builder.Environment.EnvironmentName == "ContractTests" && dependencies["ClockBaseUrl"] is not null)
    builder.Services.AddSingleton<IClock>(new HttpClock(Dependency("Clock")));
else builder.Services.AddSingleton<IClock, SystemClock>();
var leaseSeconds = configuration.GetValue("Runtime:LeaseSeconds", 300);
if (leaseSeconds <= 0) throw new InvalidOperationException("Runtime:LeaseSeconds must be positive.");
builder.Services.AddSingleton(new LeaseSettings(TimeSpan.FromSeconds(leaseSeconds)));
builder.Services.AddSingleton<AppealEligibility>();
builder.Services.AddSingleton<CheckQueries>();
builder.Services.AddSingleton<ReceiveReceipt>();
builder.Services.AddSingleton<CheckAssignments>();
builder.Services.AddSingleton<RecordVerdict>();
builder.Services.AddSingleton<CorrectVerdict>();
builder.Services.AddSingleton<QuotaCommands>();
builder.Services.AddSingleton<OpenAppeal>();
builder.Services.AddSingleton<AppealAssignments>();
builder.Services.AddSingleton<ResolveAppeal>();
builder.Services.AddSingleton<ReviewCompletion>();
builder.Services.AddSingleton<CompletionRefresh>();
builder.Services.AddHostedService<BackgroundCompletion>();
builder.Services.AddSingleton<OutboxDispatcher>();
builder.Services.AddSingleton<IOutboxDelivery>(sp => sp.GetRequiredService<OutboxDispatcher>());
builder.Services.AddHostedService<BackgroundPublication>();
var app = builder.Build();
app.UseRouting();
app.UseMiddleware<RequestMiddleware>();
app.MapGet("/health", () => Results.Json(new { service = "review", healthy = true }));
CheckEndpoints.Map(app); AppealEndpoints.Map(app); CompletionEndpoints.Map(app);
app.Run();
