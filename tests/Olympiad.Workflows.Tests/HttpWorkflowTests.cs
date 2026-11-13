using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Olympiad.Workflows.Application;
using Olympiad.Workflows.Domain;
using Olympiad.Workflows.Infrastructure;
using Xunit;

namespace Olympiad.Workflows.Tests;

public sealed class HttpWorkflowTests
{
    [Fact]
    public async Task RequestIsStoredBeforeHandlerAndOnlyCheckpointClearsIt()
    {
        await using var host = await Host.Start();
        using var first = await host.Send("/answers/task?number=7", "{\"value\":42}", "once");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = first.Headers.GetValues("Operation-Id").Single();
        var store = host.App.Services.GetRequiredService<IProcessStore>();
        var waiting = await store.FindAsync(id, "observer", default);
        Assert.Equal(ProcessStatus.AwaitingCheckpoint, waiting!.Status);
        Assert.NotNull(waiting.Invocation);
        Assert.Equal("7", host.State.Query);
        Assert.Equal("task", host.State.Route);
        Assert.Null(host.State.Authorization);
        Assert.Equal("observer", host.State.Actor);
        Assert.Equal("token", host.State.Assignment);
        Assert.True(host.State.SavedBeforeExecution);
        Assert.Equal(42, host.State.Value);
        using var replay = await host.Send("/answers/task?number=7", "{ \"value\" : 42 }", "once");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, host.State.Effects);
        using var changed = await host.Send("/answers/task?number=7", "{\"value\":43}", "once");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        await store.ConfirmAsync("ReviewRegistered", "task", default);
        var done = await store.FindAsync(id, "observer", default);
        Assert.Null(done!.Invocation);
        using var own = await host.Get("/operations/" + id, "observer");
        var view = await own.Content.ReadAsStringAsync();
        Assert.Contains("Completed", view);
        Assert.DoesNotContain("Arguments", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", view);
        using var other = await host.Get("/operations/" + id, "other");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        using var admin = await host.Get("/operations/" + id, "admin");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        using var anonymous = await host.Client.GetAsync("/operations/" + id);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task CommittedEffectWithLostReplyIsRecoveredByNewHostWithoutDuplicate()
    {
        var connection = TestDatabase.For(Guid.NewGuid().ToString());
        string id;
        await using (var first = await Host.Start(connection))
        {
            first.State.LoseReply = true;
            using var response = await first.Send("/answers/task", "{\"value\":42}", "stable");
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            id = response.Headers.GetValues("Operation-Id").Single();
            Assert.Equal(1, first.State.Effects);
        }
        await using var restarted = await Host.Start(connection, worker: true);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        ProcessSnapshot? result;
        do
        {
            result = await restarted.App.Services.GetRequiredService<IProcessStore>().FindAsync(id, "observer", default);
            if (result?.Status == ProcessStatus.Completed) break;
            await Task.Delay(40);
        } while (DateTimeOffset.UtcNow < deadline);
        Assert.Equal(ProcessStatus.Completed, result!.Status);
        Assert.Null(result.Invocation);
        Assert.True(result.Attempts >= 2);
        Assert.Equal(0, restarted.State.Effects);
        using var replay = await restarted.Send("/answers/task", "{\"value\":42}", "stable");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
    }

    [Theory]
    [InlineData("{", "key", 422)]
    [InlineData("{}", "", 422)]
    public async Task InvalidInputCannotEnterDurableQueue(string body, string key, int status)
    {
        await using var host = await Host.Start();
        using var response = await host.Send("/answers/task", body, key);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("Operation-Id"));
        Assert.Equal(0, host.State.Effects);
    }

    [Fact]
    public async Task PermanentFailureCompensatesBeforePayloadIsRemoved()
    {
        var clock=new ManualClock();
        await using var host = await Host.Start(clock:clock);
        host.State.Reject = true;
        using var response = await host.Send("/answers/task", "{\"value\":42}", "reject");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var id = response.Headers.GetValues("Operation-Id").Single();
        var store = host.App.Services.GetRequiredService<IProcessStore>();
        Assert.Equal(ProcessStatus.Compensating, (await store.FindAsync(id, "observer", default))!.Status);
        var coordinator = host.App.Services.GetRequiredService<Coordinator>();
        host.State.CleanupFails = true;
        await coordinator.RunAsync(id, "cleanup1", default);
        var retry=await store.FindAsync(id,"observer",default);
        Assert.NotNull(retry!.Invocation);
        Assert.False(await coordinator.RunAsync(id,"too-early",default));
        host.State.CleanupFails = false;
        clock.UtcNow=retry.NextAttempt;
        await coordinator.RunAsync(id, "cleanup2", default);
        var failed = await store.FindAsync(id, "observer", default);
        Assert.Equal(ProcessStatus.Failed, failed!.Status);
        Assert.Null(failed.Invocation);
        Assert.Equal(2, host.State.CleanupCalls);
    }

    public sealed record Answer(int Value);
    private sealed class ManualClock : IProcessClock { public DateTimeOffset UtcNow {get;set;}=DateTimeOffset.UtcNow; }
    private sealed class Rejected : Exception;
    private sealed class State
    {
        public bool LoseReply, Reject, CleanupFails, SavedBeforeExecution;
        public int Effects, Value, CleanupCalls;
        public string? Actor, Authorization, Assignment, Route, Query;
    }

    private sealed class Host(WebApplication app, HttpClient client, State state) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public HttpClient Client { get; } = client;
        public State State { get; } = state;
        public static async Task<Host> Start(string? connection = null, bool worker = false,IProcessClock? clock=null)
        {
            connection ??= TestDatabase.For(Guid.NewGuid().ToString());
            await using (var db = new NpgsqlConnection(connection))
            {
                await db.OpenAsync();
                await using var create = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS effects(id TEXT PRIMARY KEY, value INTEGER NOT NULL)", db);
                await create.ExecuteNonQueryAsync();
            }
            var state = new State();
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            builder.Services.AddWorkflows(connection, new()
            {
                CaptureActor = c => ActorSnapshots.Capture((string)c.Items["actor"]!, [(string)c.Items["actor"]! == "admin" ? "admin" : "observer"]),
                RestoreActor = (c, json) => c.Items["actor"] = ActorSnapshots.Read(json).Id,
                Checkpoint = _ => "ReviewRegistered",
                ReplayCode = (_, code) => code == 201 ? 200 : code,
                ClassifyError = e => e is Rejected ? new(409, "CLOSED", "Closed", "orphan") : new(503, "UNAVAILABLE", "Retry"),
                RetryBase = TimeSpan.FromMilliseconds(100), CheckpointTimeout = TimeSpan.FromMilliseconds(100),
                Compensate = (_, _) => { state.CleanupCalls++; return state.CleanupFails ? Task.FromException(new IOException()) : Task.CompletedTask; }
            });
            if (!worker) builder.Services.RemoveAll<IHostedService>();
            if(clock is not null) builder.Services.AddSingleton(clock);
            var app = builder.Build(); app.UseRouting();
            app.Use(async (c, next) =>
            {
                if (!c.Request.Headers.TryGetValue("Authorization", out var actor)) { c.Response.StatusCode = 401; return; }
                c.Items["actor"] = actor.ToString(); await next(c);
            });
            await app.UseWorkflowsAsync();
            app.MapPost("/answers/{id}", async (string id, Answer body, HttpContext c, IProcessStore processes) =>
            {
                state.Value = body.Value; state.Actor = (string)c.Items["actor"]!;
                state.Authorization = c.Request.Headers.Authorization.FirstOrDefault();
                state.Assignment = c.Request.Headers["Assignment-Token"].ToString();
                state.Route = id; state.Query = c.Request.Query["number"];
                await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
                await using (var saved = new NpgsqlCommand("SELECT COUNT(*) FROM processes WHERE operation_key=@key", db))
                {
                    saved.Parameters.AddWithValue("key", c.Request.Headers["Idempotency-Key"].ToString());
                    state.SavedBeforeExecution = (long)(await saved.ExecuteScalarAsync())! == 1;
                }
                if (state.Reject) throw new Rejected();
                await using (var effect = new NpgsqlCommand("INSERT INTO effects VALUES(@id,@value) ON CONFLICT DO NOTHING", db))
                {
                    effect.Parameters.AddWithValue("id", id); effect.Parameters.AddWithValue("value", body.Value);
                    state.Effects += await effect.ExecuteNonQueryAsync();
                }
                if (state.LoseReply) throw new IOException("Acknowledgement lost after commit");
                if (worker) await processes.ConfirmAsync("ReviewRegistered", id, default);
                return Results.Json(new { id, body.Value }, statusCode: 201);
            });
            await app.StartAsync(); return new(app, app.GetTestClient(), state);
        }
        public Task<HttpResponseMessage> Send(string path, string body, string key) => Request(HttpMethod.Post, path, "observer", body, key);
        public Task<HttpResponseMessage> Get(string path, string actor) => Request(HttpMethod.Get, path, actor);
        private async Task<HttpResponseMessage> Request(HttpMethod method, string path, string actor, string? body = null, string? key = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Authorization", actor);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            request.Headers.Add("Assignment-Token", "token");
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return await Client.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }
}
