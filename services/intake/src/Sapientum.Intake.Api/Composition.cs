using System.Net.Http.Headers;
using Sapientum.Intake.Application;
using Sapientum.Intake.Infrastructure.Http;
using Sapientum.Intake.Infrastructure.Images;
using Sapientum.Intake.Infrastructure.Persistence;

namespace Sapientum.Intake.Api;

internal static class Composition
{
    public static void AddIntake(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var connectionString = builder.Configuration["Storage:ConnectionString"] ?? "Data Source=.data/intake.db;Default Timeout=30";
        services.AddSingleton(new SqliteDatabase(connectionString));
        services.AddSingleton<IIntakeUnitOfWork>(provider => provider.GetRequiredService<SqliteDatabase>());
        HttpClient Client(string key)
        {
            var address = builder.Configuration[$"Dependencies:{key}BaseUrl"] ?? builder.Configuration["Dependencies:BaseUrl"];
            if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException($"Configure Dependencies:{key}BaseUrl or Dependencies:BaseUrl.");
            var client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            { BaseAddress = new Uri(address.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
            var accessToken = builder.Configuration["Dependencies:AccessToken"];
            if (!string.IsNullOrWhiteSpace(accessToken)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return client;
        }
        void AddDependency<TPort>(string key, Func<HttpClient, TPort> adapter) where TPort : class
        {
            services.AddKeyedSingleton<HttpClient>(key, (_, _) => Client(key));
            services.AddSingleton<TPort>(provider => adapter(provider.GetRequiredKeyedService<HttpClient>(key)));
        }
        AddDependency<IAdmissionGateway>("Games", client => new HttpAdmissionGateway(client));
        AddDependency<ITeamAdmissionGateway>("Teams", client => new HttpTeamAdmissionGateway(client));
        AddDependency<IBlankRegistry>("Materials", client => new HttpBlankRegistry(client));
        AddDependency<IIdentityProvider>("Identity", client => new HttpIdentityProvider(client));
        AddDependency<IImageStorage>("Images", client => new HttpImageStorage(client));
        AddDependency<IEventPublisher>("Events", client => new HttpEventPublisher(client));
        if (!string.IsNullOrWhiteSpace(builder.Configuration["Dependencies:ClockBaseUrl"]))
        {
            if (!builder.Environment.IsEnvironment("ContractTests"))
                throw new InvalidOperationException("External clock is permitted only in the ContractTests environment.");
            AddDependency<IClock>("Clock", client => new HttpContractClock(client));
        }
        else services.AddSingleton<IClock, SystemClock>();
        var maximumImageBytes = builder.Configuration.GetValue("Runtime:MaximumImageBytes", 15 * 1024 * 1024);
        if (maximumImageBytes < 1 || maximumImageBytes > 15 * 1024 * 1024)
            throw new InvalidOperationException("Runtime:MaximumImageBytes must be between 1 and 15728640 bytes.");
        services.AddSingleton(new ImageLimits(maximumImageBytes));
        services.AddSingleton<IImageQrDecoder, SkiaQrDecoder>();
        services.AddSingleton<AcceptBlank>(); services.AddSingleton<SubmissionFinalizer>();
        services.AddSingleton<SynchronizeIntake>(); services.AddSingleton<CloseIntake>();
        services.AddSingleton<GetReceipt>(); services.AddSingleton<GetIntakeSession>();
        services.AddSingleton<DeliverPendingEvent>(); services.AddSingleton<RecoverPendingSubmission>();
        services.AddHostedService<OutboxWorker>(); services.AddHostedService<SubmissionRecoveryWorker>();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 24 * 1024 * 1024);
    }
}
