using System.Net.Http.Headers;
using Olympiad.Intake.Application;
using Olympiad.Intake.Infrastructure.Http;
using Olympiad.Intake.Infrastructure.Images;
using Olympiad.Intake.Infrastructure.Persistence;
using Olympiad.Storage.S3;

namespace Olympiad.Intake.Api;

internal static class Composition
{
    internal sealed record Storage(PostgresDatabase Database,S3Objects? Objects);
    public static Storage AddIntake(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var connectionString = builder.Configuration["Storage:ConnectionString"] ?? throw new InvalidOperationException("Configure Storage:ConnectionString for PostgreSQL.");
        var database=new PostgresDatabase(connectionString); S3Objects? objects=null;
        services.AddSingleton(database);
        services.AddSingleton<IIntakeUnitOfWork>(provider => provider.GetRequiredService<PostgresDatabase>());
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
        if (builder.Environment.IsEnvironment("ContractTests") || builder.Environment.IsEnvironment("Testing"))
        {
            // The existing contract fixture injects HTTP failures independently from the S3 integration suite.
            AddDependency<IImageStorage>("Images", client => new HttpImageStorage(client));
            services.AddSingleton<IImageReader>(provider => (IImageReader)provider.GetRequiredService<IImageStorage>());
        }
        else
        {
            string Setting(string key) => builder.Configuration["S3:" + key] ?? throw new InvalidOperationException("Configure S3:" + key);
            var client = new S3Options(Setting("Endpoint"), Setting("Region"), Setting("AccessKey"), Setting("SecretKey"), "answer-images").CreateClient();
            services.AddSingleton(client);
            objects=new S3Objects(client,"answer-images"); services.AddSingleton(objects);
            services.AddSingleton<S3ImageStorage>();
            services.AddSingleton<IImageStorage>(provider => provider.GetRequiredService<S3ImageStorage>());
            services.AddSingleton<IImageReader>(provider => provider.GetRequiredService<S3ImageStorage>());
        }
        if(objects is not null && builder.Configuration.GetValue("Workflows:Enabled",true))
        {
            services.AddKeyedSingleton<HttpClient>("Review",(_,_)=>Client("Review"));
            services.AddSingleton<IEventPublisher>(provider=>new ReviewDelivery(provider.GetRequiredKeyedService<HttpClient>("Review"),
                provider.GetRequiredService<Olympiad.Workflows.Application.IProcessStore>(),provider.GetRequiredService<S3Objects>()));
        }
        else AddDependency<IEventPublisher>("Events", client => new HttpEventPublisher(client,objects));
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
        services.AddSingleton<GetReceipt>(); services.AddSingleton<GetIntakeSession>(); services.AddSingleton<GetImage>();
        services.AddSingleton<DeliverPendingEvent>(); services.AddSingleton<RecoverPendingSubmission>();
        services.AddHostedService<OutboxWorker>(); services.AddHostedService<SubmissionRecoveryWorker>();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 24 * 1024 * 1024);
        return new(database,objects);
    }
}
