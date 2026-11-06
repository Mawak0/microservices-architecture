using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sapientum.Intake.Application;
using Sapientum.Intake.Domain;
using Sapientum.Intake.Infrastructure.Http;
using Sapientum.Intake.Infrastructure.Persistence;
using SkiaSharp;
using Xunit;
using ZXing;
using ZXing.QrCode;

namespace Sapientum.Intake.Tests;

/// <summary>Exercises the real HTTP pipeline, use cases, QR reader and SQLite in one instrumented process.</summary>
public sealed class ApiTests
{
    [Fact]
    public async Task BackgroundDeliveryRecoversFromTransportFailureWithoutLosingAcceptedAnswer()
    {
        await using var app = new ApiFixture(); app.Backend.EventsAvailable = false;
        using var client = app.CreateClient();
        await app.Send(client, HttpMethod.Put, "/intake-sessions/game", new { admissionVersion = 1 });
        using var accepted = await app.Send(client, HttpMethod.Post, "/receipts", app.Blank());
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var answer = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        var attempted = await app.Backend.FirstEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        app.Backend.EventsAvailable = true;
        var delivered = await app.Backend.DeliveredEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(attempted.GetProperty("eventId").GetString(), delivered.GetProperty("eventId").GetString());
        Assert.Equal(answer.GetProperty("id").GetString(), delivered.GetProperty("data").GetProperty("receiptId").GetString());
    }

    [Fact]
    public async Task StoppingHttpApplicationDuringSendPreservesClaimedEventUntilLeaseExpires()
    {
        await using var app = new ApiFixture(); app.Backend.BlockEvents = true;
        using var client = app.CreateClient();
        await app.Send(client, HttpMethod.Put, "/intake-sessions/game", new { admissionVersion = 1 });
        using var accepted = await app.Send(client, HttpMethod.Post, "/receipts", app.Blank());
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var attempted = await app.Backend.FirstEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await app.StopHostAsync();
        var now = DateTimeOffset.UtcNow;
        Assert.Null(await app.Database.ExecuteAsync((tx, ct) => tx.Outbox.ClaimAsync("replacement", now.AddSeconds(10), now.AddSeconds(40), ct), default));
        var recovered = await app.Database.ExecuteAsync((tx, ct) => tx.Outbox.ClaimAsync("replacement", now.AddSeconds(31), now.AddSeconds(61), ct), default);
        Assert.Equal(attempted.GetProperty("eventId").GetString(), recovered!.Event.EventId);
        Assert.False(app.Backend.DeliveredEvent.Task.IsCompleted);
    }

    [Fact]
    public async Task AcceptedAnswerCanBeReadReplayedAndIncludedInCumulativeClosure()
    {
        await using var app = new ApiFixture(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Send(client, HttpMethod.Put, "/intake-sessions/game", new { admissionVersion = 1 })).StatusCode);
        var body = app.Blank();
        using var accepted = await app.Send(client, HttpMethod.Post, "/receipts", body, "observer", "accept-once");
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var answer = await accepted.Content.ReadFromJsonAsync<JsonElement>(); var id = answer.GetProperty("id").GetString();
        Assert.Equal("task", answer.GetProperty("taskId").GetString());
        Assert.Equal("protected://answer", answer.GetProperty("imageRef").GetString());
        using var read = await app.Send(client, HttpMethod.Get, $"/receipts/{id}", token: "checker");
        Assert.Equal(answer.ToString(), (await read.Content.ReadFromJsonAsync<JsonElement>()).ToString());
        using var replay = await app.Send(client, HttpMethod.Post, "/receipts", body, "observer", "accept-once");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(answer.ToString(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).ToString());
        await Error(await app.Send(client, HttpMethod.Post, "/receipts", body), 409, "BLANK_ALREADY_ACCEPTED");
        await Error(await app.Send(client, HttpMethod.Post, "/receipts", app.Blank(legible: false), "observer", "accept-once"), 409, "IDEMPOTENCY_KEY_REUSED");
        using var session = await app.Send(client, HttpMethod.Get, "/intake-sessions/game");
        Assert.Single((await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("receiptIds").EnumerateArray());
        app.Backend.Deadline = DateTimeOffset.UtcNow.AddMinutes(-1); app.Backend.Version = 2;
        using var close = await app.Send(client, HttpMethod.Post, "/intake-sessions/game/close", new { admissionVersion = 2 });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Equal(id, (await close.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("receiptIds")[0].GetString());
        await Error(await app.Send(client, HttpMethod.Post, "/receipts", body), 409, "INTAKE_CLOSED");
        app.Backend.Version = 3; app.Backend.Late = true;
        Assert.Equal(HttpStatusCode.OK, (await app.Send(client, HttpMethod.Put, "/intake-sessions/game", new { admissionVersion = 3 })).StatusCode);
        await Error(await app.Send(client, HttpMethod.Post, "/intake-sessions/game/close", new { admissionVersion = 3 }), 409, "LATE_ADMISSION_ACTIVE");
    }

    [Theory]
    [InlineData(null, 401, "UNAUTHENTICATED")]
    [InlineData("inactive", 401, "UNAUTHENTICATED")]
    [InlineData("checker", 403, "FORBIDDEN")]
    [InlineData("organizer", 403, "FORBIDDEN")]
    public async Task UploadRequiresAuthenticatedObserver(string? token, int status, string code)
    {
        await using var app = new ApiFixture(); using var client = app.CreateClient();
        await Error(await app.Send(client, HttpMethod.Post, "/receipts", app.Blank(), token), status, code);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("multi")]
    public async Task AdministratorAndCombinedRolesCanSubmit(string role)
    {
        await using var app = new ApiFixture(); using var client = app.CreateClient();
        await app.Send(client, HttpMethod.Put, "/intake-sessions/game", new { admissionVersion = 1 });
        Assert.Equal(HttpStatusCode.Created, (await app.Send(client, HttpMethod.Post, "/receipts", app.Blank(), role)).StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"admissionVersion\":1,\"unknown\":true}")]
    [InlineData("{\"admissionVersion\":\"one\"}")]
    [InlineData("{\"admissionVersion\":0}")]
    public async Task InvalidSessionRequestsReturnContractError(string json)
    {
        await using var app = new ApiFixture(); using var client = app.CreateClient();
        await Error(await app.SendRaw(client, "/intake-sessions/game", json), 422, "VALIDATION_ERROR");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-long")]
    public async Task MutationRequiresBoundedIdempotencyKey(string? key)
    {
        await using var app = new ApiFixture(); using var client = app.CreateClient();
        await Error(await app.Send(client, HttpMethod.Put, "/intake-sessions/game", new { admissionVersion = 1 }, key: key == "too-long" ? new string('x', 201) : key), 422, "VALIDATION_ERROR");
    }

    [Theory]
    [InlineData("null-fields", "VALIDATION_ERROR")]
    [InlineData("base64", "INVALID_IMAGE")]
    [InlineData("empty", "INVALID_IMAGE")]
    [InlineData("format", "VALIDATION_ERROR")]
    [InlineData("unconfirmed", "LEGIBILITY_NOT_CONFIRMED")]
    [InlineData("mismatch", "QR_MISMATCH")]
    [InlineData("forged", "INVALID_QR")]
    public async Task RejectedPhotographDoesNotCreateAcceptedAnswer(string kind, string code)
    {
        await using var app = new ApiFixture(); using var client = app.CreateClient();
        await app.Send(client, HttpMethod.Put, "/intake-sessions/game", new { admissionVersion = 1 });
        object body = kind switch
        {
            "null-fields" => new { qrToken = (string?)null, imageBase64 = "", imageMediaType = "image/png", legibilityConfirmed = true },
            "base64" => new { qrToken = "registered", imageBase64 = "not-base64!", imageMediaType = "image/png", legibilityConfirmed = true },
            "empty" => new { qrToken = "registered", imageBase64 = "", imageMediaType = "image/png", legibilityConfirmed = true },
            "format" => app.Blank(mediaType: "image/gif"),
            "unconfirmed" => app.Blank(legible: false),
            "mismatch" => app.Blank(token: "other"),
            "forged" => app.Blank(token: "unknown", photographedToken: "unknown"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        await Error(await app.Send(client, HttpMethod.Post, "/receipts", body), 422, code);
        using var session = await app.Send(client, HttpMethod.Get, "/intake-sessions/game");
        Assert.Empty((await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("receiptIds").EnumerateArray());
    }

    [Fact]
    public async Task MissingResourcesAndUnknownRoutesHaveNoSuccessfulResponse()
    {
        await using var app = new ApiFixture(); using var client = app.CreateClient();
        await Error(await app.Send(client, HttpMethod.Get, "/receipts/missing"), 404, "NOT_FOUND");
        await Error(await app.Send(client, HttpMethod.Get, "/intake-sessions/missing"), 404, "NOT_FOUND");
        await Error(await app.Send(client, HttpMethod.Post, "/intake-sessions/game/close", new { admissionVersion = 1 }), 404, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/not-an-endpoint")).StatusCode);
    }

    public static IEnumerable<object[]> DomainErrors() => new[]
    {
        (IntakeError.ValidationError,422,"VALIDATION_ERROR"), (IntakeError.InvalidImage,422,"INVALID_IMAGE"),
        (IntakeError.QrUnreadable,422,"QR_UNREADABLE"), (IntakeError.QrMismatch,422,"QR_MISMATCH"),
        (IntakeError.InvalidQr,422,"INVALID_QR"), (IntakeError.LegibilityNotConfirmed,422,"LEGIBILITY_NOT_CONFIRMED"),
        (IntakeError.BlankAlreadyAccepted,409,"BLANK_ALREADY_ACCEPTED"), (IntakeError.IntakeClosed,409,"INTAKE_CLOSED"),
        (IntakeError.TeamNotAdmitted,409,"TEAM_NOT_ADMITTED"), (IntakeError.IdempotencyKeyReused,409,"IDEMPOTENCY_KEY_REUSED"),
        (IntakeError.AdmissionVersionMismatch,409,"ADMISSION_VERSION_MISMATCH"), (IntakeError.DeadlineNotReached,409,"DEADLINE_NOT_REACHED"),
        (IntakeError.LateAdmissionActive,409,"LATE_ADMISSION_ACTIVE"), (IntakeError.NotFound,404,"NOT_FOUND"),
        (IntakeError.StorageUnavailable,503,"STORAGE_UNAVAILABLE"), (IntakeError.DependencyUnavailable,503,"DEPENDENCY_UNAVAILABLE"),
        (IntakeError.IntakeBusy,503,"INTAKE_BUSY")
    }.Select(x => new object[] { x.Item1, x.Item2, x.Item3 });

    [Theory, MemberData(nameof(DomainErrors))]
    public async Task DomainFailuresAreMappedAtHttpBoundary(IntakeError error, int status, string code)
    {
        await using var app = new ApiFixture(); app.Backend.Failure = new IntakeException(error, "Expected rejection");
        using var client = app.CreateClient();
        await Error(await app.Send(client, HttpMethod.Get, "/intake-sessions/game"), status, code);
    }

    [Theory]
    [InlineData("timeout", 503, "DEPENDENCY_UNAVAILABLE")]
    [InlineData("request", 422, "VALIDATION_ERROR")]
    [InlineData("unexpected", 503, "STORAGE_UNAVAILABLE")]
    public async Task InfrastructureFailuresDoNotLeakPrivateDetails(string kind, int status, string code)
    {
        await using var app = new ApiFixture(); app.Backend.Failure = kind switch
        {
            "timeout" => new OperationCanceledException("private dependency details"),
            "request" => new BadHttpRequestException("private request details"),
            _ => new InvalidOperationException("private database details")
        };
        using var client = app.CreateClient(); using var response = await app.Send(client, HttpMethod.Get, "/intake-sessions/game");
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("private", text);
        await Error(response, status, code);
    }

    private static async Task Error(HttpResponseMessage response, int status, string code)
    {
        using (response) { Assert.Equal(status, (int)response.StatusCode); Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()); }
    }

    private sealed class ApiFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sapientum-http-" + Guid.NewGuid());
        private readonly WebApplicationFactory<Program> factory;
        private bool stopped;
        public SqliteDatabase Database { get; }
        public Dependencies Backend { get; } = new();
        public ApiFixture()
        {
            Directory.CreateDirectory(directory);
            Database = new SqliteDatabase($"Data Source={Path.Combine(directory, "intake.db")};Pooling=False");
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<SqliteDatabase>();
                    services.AddSingleton(Database);
                    foreach (var name in new[] { "Games", "Teams", "Materials", "Identity", "Images", "Events" })
                        services.AddKeyedSingleton<HttpClient>(name, (_, _) => new HttpClient(Backend, disposeHandler: false) { BaseAddress = new Uri("https://dependencies.example/") });
                    services.AddSingleton<IIdentityProvider>(provider => new FaultInjectingIdentity(
                        new HttpIdentityProvider(provider.GetRequiredKeyedService<HttpClient>("Identity")), Backend));
                });
            });
        }
        public HttpClient CreateClient() => factory.CreateClient();
        public object Blank(string token = "registered", string photographedToken = "registered", string mediaType = "image/png", bool legible = true)
        {
            var matrix = new QRCodeWriter().encode(photographedToken, BarcodeFormat.QR_CODE, 240, 240);
            using var bitmap = new SKBitmap(matrix.Width, matrix.Height);
            for (var y = 0; y < matrix.Height; y++) for (var x = 0; x < matrix.Width; x++) bitmap.SetPixel(x, y, matrix[x,y] ? SKColors.Black : SKColors.White);
            using var image = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return new { qrToken = token, imageBase64 = Convert.ToBase64String(image.ToArray()), imageMediaType = mediaType, legibilityConfirmed = legible };
        }
        public async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body = null, string? token = "admin", string? key = "unique")
        {
            using var request = new HttpRequestMessage(method, path);
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            if (key is not null) request.Headers.Add("Idempotency-Key", key == "unique" ? Guid.NewGuid().ToString() : key);
            if (body is not null) request.Content = JsonContent.Create(body);
            return await client.SendAsync(request);
        }
        public async Task<HttpResponseMessage> SendRaw(HttpClient client, string path, string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new("Bearer", "admin"); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return await client.SendAsync(request);
        }
        public async ValueTask StopHostAsync() { if (!stopped) { await factory.DisposeAsync(); stopped = true; } }
        public async ValueTask DisposeAsync() { await StopHostAsync(); Backend.Dispose(); Directory.Delete(directory, true); }
    }

    private sealed class FaultInjectingIdentity(IIdentityProvider inner, Dependencies dependencies) : IIdentityProvider
    {
        public Task<Actor?> IntrospectAsync(string token, CancellationToken ct) => dependencies.Failure is { } error ? throw error : inner.IntrospectAsync(token, ct);
    }
    private sealed class Dependencies : HttpMessageHandler
    {
        public Exception? Failure { get; set; }
        public DateTimeOffset Deadline { get; set; } = DateTimeOffset.UtcNow.AddHours(1);
        public int Version { get; set; } = 1;
        public bool Late { get; set; }
        public bool EventsAvailable { get; set; } = true;
        public bool BlockEvents { get; set; }
        public TaskCompletionSource<JsonElement> FirstEvent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> DeliveredEvent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? default : await request.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (path == "/events")
            {
                // Capture the failure before waking the test that restores the transport.
                var available = EventsAvailable;
                FirstEvent.TrySetResult(body.Clone());
                if (BlockEvents) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (!available) return new(HttpStatusCode.ServiceUnavailable);
                DeliveredEvent.TrySetResult(body.Clone());
            }
            object result = path switch
            {
                "/auth/introspect" => Identity(body.GetProperty("token").GetString()!),
                "/games/game" => new { id = "game", admissionVersion = Version, status = "running", submissionDeadline = Deadline, lateSubmissionAllowed = Late },
                "/teams/team" => new { id = "team", gameId = "game", admitted = true },
                "/blank-verifications" => new { id = "blank", gameId = "game", teamId = "team", taskId = "task", taskNumber = "1" },
                "/images" => new { imageRef = "protected://answer" },
                "/events" => new { },
                _ => throw new InvalidOperationException("Unexpected dependency request: " + path)
            };
            if (path == "/blank-verifications" && body.GetProperty("qrToken").GetString() == "unknown")
                return new(HttpStatusCode.UnprocessableEntity) { Content = JsonContent.Create(new { code = "INVALID_QR" }) };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
        private static object Identity(string token) => new { active = token != "inactive", userId = token,
            roles = token == "multi" ? new[] { "checker", "observer" } : new[] { token } };
    }
}
