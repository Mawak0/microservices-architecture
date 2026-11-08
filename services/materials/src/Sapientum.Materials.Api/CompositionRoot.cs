using PdfSharp.Fonts;
using Sapientum.Materials.Application;
using Sapientum.Materials.Infrastructure.Http;
using Sapientum.Materials.Infrastructure.Pdf;
using Sapientum.Materials.Infrastructure.Persistence;
using Sapientum.Materials.Infrastructure.Security;
using Sapientum.Materials.Infrastructure.Templates;
using System.Net.Http.Headers;

namespace Sapientum.Materials.Api;

/// <summary>The only API file permitted to name infrastructure implementations.</summary>
internal static class CompositionRoot
{
    public static async Task ConfigureAsync(WebApplicationBuilder builder, CancellationToken cancellationToken)
    {
        var config = builder.Configuration;
        var database = new SqliteDatabase(config["Storage:ConnectionString"] ?? "Data Source=.data/materials.db");
        await database.InitializeAsync(cancellationToken);
        var configuredKey = config["Security:QrSigningKeyBase64"] is { } encoded ? Convert.FromBase64String(encoded) : null;
        var key = await database.GetOrCreateSigningKeyAsync(configuredKey, cancellationToken);
        GlobalFontSettings.FontResolver ??= new UnicodeFontResolver(config["Pdf:FontPath"]);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton<IMaterialsTransactions, SqliteMaterialsTransactions>();
        builder.Services.AddSingleton<IBlankTokenProtector>(new HmacBlankTokenProtector(key));
        builder.Services.AddSingleton<IPdfRenderer, PdfSharpMaterialRenderer>();
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
    }
}
