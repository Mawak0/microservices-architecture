using System.Net;
using System.Text;
using Sapientum.Intake.Application;
using Sapientum.Intake.Domain;
using Sapientum.Intake.Infrastructure.Http;
using Xunit;

namespace Sapientum.Intake.Tests;

public sealed class HttpAdapterTests
{
    [Fact]
    public async Task BlankVerificationHasIndividualIdempotencyKeysAndDependencyAuthentication()
    {
        var handler = new RecordingHandler("""{"id":"blank","gameId":"game","teamId":"team","taskId":"task","taskNumber":"1"}""");
        using var client = Client(handler); var gateway = new HttpBlankRegistry(client);
        Assert.Equal("task", (await gateway.VerifyAsync("token", default)).TaskId);
        await gateway.VerifyAsync("other-token", default);
        Assert.Equal(new[] { "/blank-verifications", "/blank-verifications" }, handler.Paths);
        Assert.All(handler.Authorization, value => Assert.Equal("Bearer dependency-token", value));
        Assert.All(handler.Keys, key => Assert.False(string.IsNullOrEmpty(key)));
        Assert.NotEqual(handler.Keys[0], handler.Keys[1]);
    }
    [Fact]
    public async Task ImageRetryUsesReceiptIdentityAndUnmodifiedBytes()
    {
        var handler = new RecordingHandler("""{"imageRef":"protected://image"}""");
        using var client = Client(handler); var gateway = new HttpImageStorage(client);
        var bytes = new byte[] { 7, 8, 9 };
        await gateway.SaveAsync("receipt", "blank", bytes, "image/png", default);
        await gateway.SaveAsync("receipt", "blank", bytes, "image/png", default);
        Assert.Equal(new[] { "receipt", "receipt" }, handler.Keys);
        Assert.Contains(Convert.ToBase64String(bytes), handler.Bodies[0]);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
    }
    [Fact]
    public async Task AuthenticationRolesAreSuppliedByProviderRatherThanTokenNames()
    {
        var handler = new RecordingHandler("""{"active":true,"userId":"user-42","roles":["observer","checker"]}""");
        using var client = Client(handler);
        var actor = await new HttpIdentityProvider(client).IntrospectAsync("opaque-provider-token", default);
        Assert.Equal("user-42", actor!.UserId); Assert.Contains("observer", actor.Roles); Assert.Contains("checker", actor.Roles);
        Assert.Contains("opaque-provider-token", handler.Bodies.Single());
    }
    [Fact]
    public async Task InvalidRegistryQrIsDomainRejectionRatherThanStorageFailure()
    {
        var handler = new RecordingHandler("{}") { Status = HttpStatusCode.UnprocessableEntity };
        using var client = Client(handler);
        Assert.Equal(IntakeError.InvalidQr, (await Assert.ThrowsAsync<IntakeException>(() => new HttpBlankRegistry(client).VerifyAsync("token", default))).Error);
    }
    [Fact]
    public async Task EventDeliveryUsesStableEventIdInHeaderAndEnvelope()
    {
        var handler = new RecordingHandler("{}") { Status = HttpStatusCode.Accepted };
        using var client = Client(handler);
        var message = new IntakeClosed("stable-event", DateTimeOffset.UtcNow, "game", new("game", 3, ["receipt"]));
        var publisher = new HttpEventPublisher(client);
        await publisher.PublishAsync(message, default); await publisher.PublishAsync(message, default);
        Assert.Equal(new[] { "stable-event", "stable-event" }, handler.Keys);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
    }
    private static HttpClient Client(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://dependency.example/") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dependency-token"); return client;
    }
    private sealed class RecordingHandler(string result) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public List<string> Paths { get; } = [];
        public List<string?> Authorization { get; } = [];
        public List<string?> Keys { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath); Authorization.Add(request.Headers.Authorization?.ToString());
            Keys.Add(request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new(Status) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }
}
