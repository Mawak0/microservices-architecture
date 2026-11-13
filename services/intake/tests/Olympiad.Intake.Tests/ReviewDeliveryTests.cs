using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Olympiad.Intake.Domain;
using Olympiad.Intake.Infrastructure.Http;
using Olympiad.Persistence.Postgres;
using Olympiad.Storage.S3;
using Olympiad.Workflows.Domain;
using Olympiad.Workflows.Infrastructure;
using Xunit;
using Process = Olympiad.Workflows.Domain.Process;

namespace Olympiad.Intake.Tests;

public sealed class ReviewDeliveryTests
{
    [Fact]
    public async Task OnlyMatchingCommittedCheckingResultAcknowledgesSavedSubmission()
    {
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid().ToString();
        var processes = new PostgresProcesses(new PostgresStore(TestDatabase.For(id))); await processes.InitializeAsync(default);
        await processes.AcceptAsync(Process.Accept(id, "observer", "POST:/receipts", "key", "hash",
            new("/receipts", "POST", "/receipts", "{}", "{}", "{}", [1]), "ReviewRegistered", now), default);
        await processes.ClaimAsync(id, "worker", now, TimeSpan.FromMinutes(1), default);
        await processes.FinishAsync(id, "worker", new(new(201, "application/json", [], "receipt")), now, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), default);
        var receipt = Receipt.Restore("receipt", new("blank", "game", "team", "task", "1"), 1, "/receipts/receipt/image", now);
        var message = ReceiptAccepted.From(receipt, "event") with { OperationId = id };
        using var handler = new Receiver(); using var http = new HttpClient(handler) { BaseAddress = new("http://review/") };
        var options = Options(); using var s3 = options.CreateClient(); var objects = new S3Objects(s3, options.Bucket); await objects.InitializeAsync(default);
        var delivery = new ReviewDelivery(http, processes, objects);
        foreach (var fault in new[] { "unavailable", "operation", "version", "checkpoint", "body" })
        {
            handler.Fault = fault;
            await Assert.ThrowsAsync<IntakeException>(() => delivery.PublishAsync(message, default));
            Assert.NotNull((await processes.FindAsync(id, "observer", default))!.Invocation);
        }
        handler.Fault = null; await delivery.PublishAsync(message, default); await delivery.PublishAsync(message, default);
        Assert.Equal("event", handler.Key);
        Assert.Equal(id, handler.Message.GetProperty("operationId").GetString());
        Assert.Equal(ProcessStatus.Completed, (await processes.FindAsync(id, "observer", default))!.Status);
        Assert.Null((await processes.FindAsync(id, "observer", default))!.Invocation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processes.ConfirmOperationAsync(id, 2, "ReviewRegistered", "receipt", default));
    }

    [Fact]
    public async Task AbandonedUploadDeletesOnlyItsOwnObjectsAndRetriesAreHarmless()
    {
        var options = Options(); using var s3 = options.CreateClient(); var objects = new S3Objects(s3, options.Bucket); await objects.InitializeAsync(default);
        var owner = Guid.NewGuid().ToString(); var other = await objects.PutAsync(Guid.NewGuid().ToString(), new byte[] { 9 }, "image/png", default);
        var abandoned = await objects.PutAsync(owner, new byte[] { 1, 2 }, "image/png", default);
        var processes = new PostgresProcesses(new PostgresStore(TestDatabase.For(owner)));
        using var http = new HttpClient(new Receiver()) { BaseAddress = new("http://review/") };
        var delivery = new ReviewDelivery(http, processes, objects);
        var message = new UploadAbandoned("cleanup", DateTimeOffset.UtcNow, owner);
        Assert.IsType<UploadAbandoned>(Olympiad.Intake.Infrastructure.Persistence.EventSerialization.Deserialize(
            Olympiad.Intake.Infrastructure.Persistence.EventSerialization.Serialize(message)));
        await delivery.PublishAsync(message, default); await delivery.PublishAsync(message, default);
        await Assert.ThrowsAsync<Amazon.S3.AmazonS3Exception>(() => objects.ReadAsync(abandoned, default));
        Assert.Equal(new byte[] { 9 }, (await objects.ReadAsync(other, default)).Bytes);
        await objects.DeleteAsync(other.Key, default);
    }

    private static S3Options Options() => new(Environment.GetEnvironmentVariable("TEST_S3_ENDPOINT")!, "us-east-1",
        Environment.GetEnvironmentVariable("TEST_S3_ACCESS_KEY")!, Environment.GetEnvironmentVariable("TEST_S3_SECRET_KEY")!, "answer-images");
    private sealed class Receiver : HttpMessageHandler
    {
        public string? Fault, Key;
        public JsonElement Message;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/integrations/receipts", request.RequestUri!.AbsolutePath);
            Key = request.Headers.GetValues("Idempotency-Key").Single();
            Message = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var response = new HttpResponseMessage(Fault == "unavailable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created)
            { Content = JsonContent.Create(new { id = "check", receiptId = Fault == "body" ? "wrong" : "receipt", blankId = "blank", taskId = "task", gameId = "game" }) };
            response.Headers.Add("Operation-Id", Fault == "operation" ? "wrong" : Message.GetProperty("operationId").GetString());
            response.Headers.Add("Process-Version", Fault == "version" ? "2" : "1");
            response.Headers.Add("Checkpoint", Fault == "checkpoint" ? "other" : "ReviewRegistered");
            return response;
        }
    }
}
