using PdfSharp.Fonts;
using Olympiad.Materials.Application;
using Olympiad.Materials.Infrastructure.Http;
using Olympiad.Materials.Infrastructure.Pdf;
using Olympiad.Materials.Infrastructure.Persistence;
using Olympiad.Materials.Infrastructure.Security;
using Olympiad.Materials.Infrastructure.Templates;
using System.Net.Http.Headers;
using Olympiad.Storage.S3;

namespace Olympiad.Materials.Api;

/// <summary>The only API file permitted to name infrastructure implementations.</summary>
internal static class CompositionRoot
{
    public static async Task ConfigureAsync(WebApplicationBuilder builder, CancellationToken cancellationToken)
    {
        var config = builder.Configuration;
        var database = new PostgresDatabase(config["Storage:ConnectionString"] ?? throw new InvalidOperationException("Configure Storage:ConnectionString for PostgreSQL."));
        await database.InitializeAsync(cancellationToken);
        var configuredKey = config["Security:QrSigningKeyBase64"] is { } encoded ? Convert.FromBase64String(encoded) : null;
        var key = await database.GetOrCreateSigningKeyAsync(configuredKey, cancellationToken);
        GlobalFontSettings.FontResolver ??= new UnicodeFontResolver(config["Pdf:FontPath"]);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton<IMaterialsTransactions, PostgresMaterialsTransactions>();
        builder.Services.AddSingleton<IBlankTokenProtector>(new HmacBlankTokenProtector(key));
        builder.Services.AddSingleton<IPdfRenderer, PdfSharpMaterialRenderer>();
        string S3Setting(string key) => config["S3:" + key] ?? throw new InvalidOperationException("Configure S3:" + key);
        var s3Client = new S3Options(S3Setting("Endpoint"), S3Setting("Region"), S3Setting("AccessKey"), S3Setting("SecretKey"), "print-materials").CreateClient();
        builder.Services.AddSingleton(s3Client);
        var objects = new S3Objects(s3Client, "print-materials");
        await objects.InitializeAsync(cancellationToken);
        builder.Services.AddSingleton(objects);
        builder.Services.AddSingleton<IPrintArtifactStorage, S3PrintArtifacts>();
        DependencyClient Dependency(string specific)
        {
            var baseUrl = config[$"Dependencies:{specific}BaseUrl"] ?? config["Dependencies:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException($"Configure Dependencies__{specific}BaseUrl or Dependencies__BaseUrl.");
            var client = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
            if (config["Dependencies:AccessToken"] is { Length: > 0 } token) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            builder.Services.AddSingleton(client);
            return new(client);
        }
        builder.Services.AddSingleton<IIdentityGateway>(new HttpIdentityGateway(Dependency("Identity")));
        builder.Services.AddSingleton<IGameMaterialsGateway>(new HttpGameMaterialsGateway(Dependency("Games")));
        builder.Services.AddSingleton<ITeamAdmissionGateway>(new HttpTeamAdmissionGateway(Dependency("Teams")));
        builder.Services.AddSingleton<ITaskStatementGateway>(new HttpTaskStatementGateway(Dependency("Tasks")));
        if (config["Templates:Directory"] is { } templatesDirectory)
            builder.Services.AddSingleton<ITemplateRepository>(new FileTemplateRepository(templatesDirectory));
        else if (builder.Environment.IsEnvironment("ContractTests") || config["Dependencies:TemplatesBaseUrl"] is not null)
            builder.Services.AddSingleton<ITemplateRepository>(new HttpTemplateRepository(Dependency("Templates")));
        else builder.Services.AddSingleton<ITemplateRepository>(new FileTemplateRepository(Path.Combine(AppContext.BaseDirectory, "templates")));
        builder.Services.AddSingleton<IEventPublisher>(new HttpEventPublisher(Dependency("Events")));
        if (config["Dependencies:ClockBaseUrl"] is not null)
        {
            if (!builder.Environment.IsEnvironment("ContractTests")) throw new InvalidOperationException("The external clock is allowed only in ContractTests.");
            builder.Services.AddSingleton<IClock>(new HttpContractClock(Dependency("Clock")));
        }
        else builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IssueBlanks>(services => new(
            services.GetRequiredService<IGameMaterialsGateway>(), services.GetRequiredService<ITeamAdmissionGateway>(),
            services.GetRequiredService<IMaterialsTransactions>(), services.GetRequiredService<IBlankTokenProtector>(),
            services.GetRequiredService<IClock>(), config.GetValue("Runtime:MaxBlanksPerBatch", 10000)));
        builder.Services.AddSingleton<PrepareKit>();
        builder.Services.AddSingleton<CreatePrintJob>(); builder.Services.AddSingleton<MaterialQueries>();
        builder.Services.AddSingleton<ProcessPrintJobs>(); builder.Services.AddSingleton<PublishOutbox>();
        builder.Services.AddHostedService<PrintWorker>(); builder.Services.AddHostedService<OutboxWorker>();
    }
}
