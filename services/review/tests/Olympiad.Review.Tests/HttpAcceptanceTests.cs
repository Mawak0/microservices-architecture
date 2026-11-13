using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Olympiad.Review.Tests;

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
                ["Runtime__LeaseSeconds"] = "60",
                ["Logging__LogLevel__Default"] = "Error"
            });
            await using var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseContentRoot(root).UseEnvironment("ContractTests"));
            using var client = application.CreateClient();
            await using var bridge = new HttpBridge(client);
            using var tests = StartNode(root, ["--test", "--test-concurrency=1", "tests/acceptance.test.mjs", "tests/runtime.test.mjs"], new()
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
        var receipt = scenario.GetProperty("receipts")[0];
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(receipt.GetRawText())!;
        var id = data["id"].GetString(); data.Remove("id"); data["receiptId"] = JsonSerializer.SerializeToElement(id);
        await using var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseContentRoot(root).UseEnvironment("ContractTests")
                .UseSetting("Storage:ConnectionString", TestDatabase.For("durable-http-" + Guid.NewGuid()))
                .UseSetting("Workflows:Enabled", "true").UseSetting("Logging:LogLevel:Default", "Warning"));
        using var client = application.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", "test-system");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var received = await client.PostAsJsonAsync("/integrations/receipts", new { eventId = Guid.NewGuid().ToString(), type = "ReceiptAccepted", occurredAt = DateTimeOffset.UtcNow, aggregateId = id, operationId = "source-operation", processVersion = 1, data });
        Assert.True(received.StatusCode == HttpStatusCode.Created, await received.Content.ReadAsStringAsync());
        Assert.Equal("source-operation", received.Headers.GetValues("Operation-Id").Single());
        Assert.Equal("ReviewRegistered", received.Headers.GetValues("Checkpoint").Single());
        var check = (await received.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-checker");
        using var assigned = await Olympiad.Testing.DurableHttp.Settled(() => client.PostAsync("/checks/" + check + "/claim", null));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Equal("Completed", assigned.Headers.GetValues("Operation-Status").Single());
        var token = (await assigned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lease").GetProperty("token").GetString();
        client.DefaultRequestHeaders.Add("Assignment-Token", token);
        using var verdict = await Olympiad.Testing.DurableHttp.Settled(() => client.PostAsJsonAsync("/checks/" + check + "/verdict", new { verdict = "correct" }));
        Assert.True(verdict.StatusCode==HttpStatusCode.OK, await verdict.Content.ReadAsStringAsync());
        Assert.Equal("Completed", verdict.Headers.GetValues("Operation-Status").Single());
        using var replay = await client.PostAsJsonAsync("/checks/" + check + "/verdict", new { verdict = "correct" });
        Assert.Equal(await verdict.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
    }
    private static string FindRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Olympiad.Review.sln"))) path = path.Parent;
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
