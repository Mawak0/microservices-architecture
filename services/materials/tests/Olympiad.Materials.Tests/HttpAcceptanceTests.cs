using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Olympiad.Materials.Tests;

/// <summary>Runs the unchanged HTTP contract suite against the real in-process API,
/// so the collector observes endpoints, middleware, gateways and hosted workers too.</summary>
[Collection("HTTP environment")]
public sealed class HttpAcceptanceTests
{
    [Fact]
    public async Task PublishedHttpContractPassesThroughRealApplicationAndPostgres()
    {
        var root = FindRoot();
        var directory = Path.Combine(Path.GetTempPath(), "olympiad-http-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        Process? fixtures = null;
        try
        {
            fixtures = StartNode(root, ["test-support/fixtures.mjs"],
                new() { ["FIXTURE_PORT"] = FreePort().ToString() });
            var fixtureUrl = "http://127.0.0.1:" + fixtures.StartInfo.Environment["FIXTURE_PORT"];
            using var probe = new HttpClient();
            await WaitForFixtures(probe, fixtureUrl, fixtures);
            using var environment = new EnvironmentScope(new()
            {
                ["Workflows__Enabled"] = "false",
                ["Dependencies__BaseUrl"] = fixtureUrl,
                ["Dependencies__ClockBaseUrl"] = fixtureUrl,
                ["Dependencies__AccessToken"] = "test-system",
                ["Storage__ConnectionString"] = TestDatabase.For(Path.Combine(directory, "service.db")),
                ["S3__Endpoint"] = Environment.GetEnvironmentVariable("TEST_S3_ENDPOINT") ?? throw new InvalidOperationException("Set TEST_S3_ENDPOINT"),
                ["S3__Region"] = "us-east-1",
                ["S3__AccessKey"] = Environment.GetEnvironmentVariable("TEST_S3_ACCESS_KEY") ?? throw new InvalidOperationException("Set TEST_S3_ACCESS_KEY"),
                ["S3__SecretKey"] = Environment.GetEnvironmentVariable("TEST_S3_SECRET_KEY") ?? throw new InvalidOperationException("Set TEST_S3_SECRET_KEY"),
                ["Runtime__LeaseSeconds"] = "60",
                ["Logging__LogLevel__Default"] = "Error"
            });
            await using var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseContentRoot(root).UseEnvironment("ContractTests"));
            using var client = application.CreateClient();
            await using var bridge = new HttpBridge(client);
            using var tests = StartNode(root, ["--test", "--test-concurrency=1", "tests/acceptance.test.mjs"], new()
            {
                ["SERVICE_BASE_URL"] = bridge.Url,
                ["FIXTURE_BASE_URL"] = fixtureUrl,
                ["CONTRACT_TEST_ALLOW_WRITES"] = "1"
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var stdout = tests.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = tests.StandardError.ReadToEndAsync(timeout.Token);
            try { await tests.WaitForExitAsync(timeout.Token); }
            finally { if (!tests.HasExited) tests.Kill(entireProcessTree: true); }
            Assert.True(tests.ExitCode == 0, await stdout + "\n" + await stderr);
            await VerifyDurableCommands(root, fixtureUrl);

        }
        finally
        {
            if (fixtures is not null)
            {
                if (!fixtures.HasExited) fixtures.Kill(entireProcessTree: true);
                await fixtures.WaitForExitAsync(); fixtures.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new();
        public EnvironmentScope(Dictionary<string, string> values)
        {
            foreach (var pair in values)
            {
                previous[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
        public void Dispose()
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
    private static async Task VerifyDurableCommands(string root, string fixtureUrl)
    {
        using var fixture = new HttpClient();
        using var created = await fixture.PostAsJsonAsync(fixtureUrl + "/__test/scenarios", new { });
        var scenario = await created.Content.ReadFromJsonAsync<JsonElement>();
        var game = scenario.GetProperty("game").GetProperty("id").GetString();
        var team = scenario.GetProperty("teamId").GetString();
        var task = scenario.GetProperty("taskIds")[0].GetString();
        await using var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseContentRoot(root).UseEnvironment("ContractTests").UseSetting("Workflows:Enabled", "true"));
        using var client = application.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", "test-admin");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var kit = await Olympiad.Testing.DurableHttp.Settled(() => client.PostAsJsonAsync("/kits", new { gameId = game, teamId = team }));
        Assert.Contains(kit.StatusCode,new[] {HttpStatusCode.Created,HttpStatusCode.OK});
        Assert.Equal("Completed", kit.Headers.GetValues("Operation-Status").Single());
        using var print = await client.PostAsJsonAsync("/print-jobs", new { gameId = game, teamId = team, blankIds = Array.Empty<string>(), taskIds = new[] { task } });
        Assert.Equal(HttpStatusCode.Accepted, print.StatusCode);
        var operation = print.Headers.GetValues("Operation-Location").Single();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15); string? status;
        do { using var read = await client.GetAsync(operation); status = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString();
            if (status == "Completed") break; await Task.Delay(100);
        } while (DateTimeOffset.UtcNow < deadline);
        Assert.Equal("Completed", status);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-checker");
        using var forbidden = await client.PostAsJsonAsync("/print-jobs", new { gameId = game, teamId = team, blankIds = Array.Empty<string>(), taskIds = new[] { task } });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.False(forbidden.Headers.Contains("Operation-Id"));
        var policy=application.Services.GetRequiredService<Olympiad.Workflows.Infrastructure.HttpWorkflowOptions>();
        foreach(var failure in new Exception[] {new Olympiad.Materials.Application.AccessDeniedException(),new Olympiad.Materials.Application.NotFoundException("Missing"),new Olympiad.Materials.Domain.DomainException("VALIDATION_ERROR","Invalid"),new JsonException(),new Microsoft.AspNetCore.Http.BadHttpRequestException("Invalid")})
            Assert.InRange(policy.ClassifyError(failure).Status,400,499);
        foreach(var failure in new Exception[] {new Olympiad.Materials.Application.DependencyUnavailableException("Offline"),new OperationCanceledException(),new IOException()})
            Assert.InRange(policy.ClassifyError(failure).Status,500,599);
    }
    private static string FindRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Olympiad.Materials.sln"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Service root not found.");
    }
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static Process StartNode(string root, string[] args, Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo("node") { WorkingDirectory = root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        return Process.Start(info) ?? throw new InvalidOperationException("Node did not start.");
    }
    private static async Task WaitForFixtures(HttpClient client, string url, Process process)
    {
        for (var i = 0; i < 100; i++)
        {
            if (process.HasExited) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
            try { using var response = await client.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        process.Kill(entireProcessTree: true);
        throw new TimeoutException("Fixture server did not become healthy.");
    }
    private sealed class HttpBridge : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly HttpClient client;
        private readonly Task loop;
        public string Url { get; } = "http://127.0.0.1:" + FreePort();
        public HttpBridge(HttpClient client)
        {
            this.client = client; listener.Prefixes.Add(Url + "/"); listener.Start(); loop = Forward();
        }
        private readonly List<Task> requests = [];
        private volatile bool stopping;
        private async Task Forward()
        {
            try
            {
                while (listener.IsListening)
                    requests.Add(ForwardOne(await listener.GetContextAsync()));
            }
            catch (HttpListenerException) when (stopping) { }
            catch (ObjectDisposedException) when (stopping) { }
        }
        private async Task ForwardOne(HttpListenerContext context)
        {
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), context.Request.RawUrl);
                if (context.Request.HasEntityBody)
                {
                    using var buffer = new MemoryStream(); await context.Request.InputStream.CopyToAsync(buffer);
                    request.Content = new ByteArrayContent(buffer.ToArray());
                }
                foreach (var key in context.Request.Headers.AllKeys.OfType<string>())
                {
                    if (key is "Host" or "Content-Length" or "Transfer-Encoding" or "Connection") continue;
                    if (!request.Headers.TryAddWithoutValidation(key, context.Request.Headers[key]))
                        request.Content?.Headers.TryAddWithoutValidation(key, context.Request.Headers[key]);
                }
                using var response = await client.SendAsync(request);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            finally { context.Response.Close(); }
        }
        public async ValueTask DisposeAsync()
        {
            stopping = true; listener.Close(); await loop; await Task.WhenAll(requests);
        }
    }
}

[CollectionDefinition("HTTP environment", DisableParallelization = true)]
public sealed class HttpEnvironmentCollection { }
