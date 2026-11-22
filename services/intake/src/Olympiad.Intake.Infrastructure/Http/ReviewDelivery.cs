using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;
using Olympiad.Storage.S3;
using Olympiad.Workflows.Application;

namespace Olympiad.Intake.Infrastructure.Http;

/// <summary>Only a committed receiver result acknowledges the submission's irreversible checkpoint.</summary>
public sealed class ReviewDelivery(HttpClient review,IProcessStore processes,S3Objects images) : IEventPublisher
{
    public async Task PublishAsync(IntakeEvent message,CancellationToken ct)
    {
        if(message is UploadAbandoned abandoned) { await images.DeleteOwnerAsync(abandoned.AggregateId,ct); return; }
        var path = message switch
        {
            ReceiptAccepted => "integrations/receipts",
            RetakeImageStored => "integrations/retake-images",
            IntakeClosed => "integrations/intake-closures",
            _ => throw new InvalidOperationException("Unsupported intake delivery.")
        };
        using var request=new HttpRequestMessage(HttpMethod.Post,path)
        { Content=new StringContent(Persistence.EventSerialization.Serialize(message),Encoding.UTF8,"application/json") };
        request.Headers.Add("Idempotency-Key",message.EventId);
        using var response=await review.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode) throw new IntakeException(IntakeError.DependencyUnavailable,"The checking service did not confirm durable processing.");
        if (message is RetakeImageStored retake)
        {
            var ack = await response.Content.ReadFromJsonAsync<RetakeAck>(ct);
            if (ack is null || ack.Id != retake.Data.RetakeId || ack.ReceiptId != retake.Data.ReceiptId ||
                ack.ImageVersion != retake.Data.ImageVersion || retake.OperationId is null)
                throw new IntakeException(IntakeError.DependencyUnavailable, "Review did not acknowledge the retake version.");
            await processes.ConfirmOperationAsync(retake.OperationId, retake.ProcessVersion, "RetakeRegistered", ack.Id, ct);
        }
        if(message is ReceiptAccepted accepted)
        {
            if(accepted.OperationId is null ||
                !response.Headers.TryGetValues("Operation-Id",out var operation)||operation.SingleOrDefault()!=accepted.OperationId||
                !response.Headers.TryGetValues("Process-Version",out var version)||version.SingleOrDefault()!=accepted.ProcessVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)||
                !response.Headers.TryGetValues("Checkpoint",out var checkpoint)||checkpoint.SingleOrDefault()!="ReviewRegistered")
                throw new IntakeException(IntakeError.DependencyUnavailable,"The checking service did not confirm the process identity and version.");
            var result=await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if(!result.TryGetProperty("id",out var check)||string.IsNullOrWhiteSpace(check.GetString())||
                !result.TryGetProperty("receiptId",out var receipt)||receipt.GetString()!=accepted.Data.ReceiptId||
                !result.TryGetProperty("blankId",out var blank)||blank.GetString()!=accepted.Data.BlankId||
                !result.TryGetProperty("gameId",out var game)||game.GetString()!=accepted.Data.GameId||
                !result.TryGetProperty("taskId",out var task)||task.GetString()!=accepted.Data.TaskId)
                throw new IntakeException(IntakeError.DependencyUnavailable,"The checking service did not return the committed answer identity.");
            await processes.ConfirmOperationAsync(accepted.OperationId,accepted.ProcessVersion,"ReviewRegistered",accepted.Data.ReceiptId,ct);
        }
    }

    private sealed record RetakeAck(string Id, string ReceiptId, int ImageVersion);
}
