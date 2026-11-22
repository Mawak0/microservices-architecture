using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Application;

public sealed record RetakeAuthorization(string Id, string CheckId, string ReceiptId, string BlankId, string GameId,
    int ImageVersion, bool RequiresApproval, bool Approved);
public interface IRetakeAuthorization
{
    Task<RetakeAuthorization> GetAsync(string checkId, string requestId, CancellationToken ct);
}
public sealed record StoredRetake(string Id, string ReceiptId, int ImageVersion, string ImageRef, DateTimeOffset AcceptedAt);
public sealed record UploadRetake(string CheckId, string RetakeId, string QrToken, ReadOnlyMemory<byte> Image,
    string MediaType, bool Confirmed);

public interface IReceiptImageVersions
{
    Task<int> LatestAsync(string receiptId, CancellationToken ct);
    Task<ImageReference?> FindAsync(string receiptId, int version, CancellationToken ct);
    Task AddAsync(StoredRetake retake, string actor, DateTimeOffset uploadedAt, ReadOnlyMemory<byte> image,
        string mediaType, CancellationToken ct);
}

public sealed class RetakeUpload(IIntakeUnitOfWork store, IRetakeAuthorization authorization, IAdmissionGateway games,
    IBlankRegistry registry, IImageQrDecoder decoder, IImageStorage images, IClock clock, ImageLimits limits)
{
    public async Task<StoredRetake> ExecuteAsync(string receiptId, UploadRetake command, string actorId,
        OperationIdentity operation, CancellationToken ct)
    {
        var (receipt, prior) = await store.ExecuteAsync(async (s, token) =>
        {
            var result = (await s.Operations.FindAsync(operation, token))?.Match(operation);
            return (await s.Receipts.FindAsync(receiptId, token), result);
        }, ct);
        if (receipt is null) throw new IntakeException(IntakeError.NotFound, "Accepted answer not found.");
        if (prior?.ImageVersion is { } previous) return Reply(receipt, command.RetakeId, previous);

        if (!command.Confirmed) throw new IntakeException(IntakeError.LegibilityNotConfirmed, "Confirm the new image.");
        if (command.MediaType is not ("image/png" or "image/jpeg") || command.Image.Length == 0 ||
            command.Image.Length > limits.MaximumBytes)
            throw new IntakeException(IntakeError.InvalidImage, "Invalid image type or size.");
        var tokenFromImage = decoder.Decode(command.Image, command.MediaType);
        if (tokenFromImage != command.QrToken) throw new IntakeException(IntakeError.QrMismatch, "QR differs from the image.");
        var actual = await registry.VerifyAsync(tokenFromImage, ct);
        if (actual.BlankId != receipt.Blank.BlankId || actual.TeamId != receipt.Blank.TeamId ||
            actual.GameId != receipt.Blank.GameId || actual.TaskId != receipt.Blank.TaskId)
            throw new IntakeException(IntakeError.QrMismatch, "Retake must show the accepted blank.");

        var ticket = await authorization.GetAsync(command.CheckId, command.RetakeId, ct);
        if (ticket.Id != command.RetakeId || ticket.CheckId != command.CheckId || ticket.ReceiptId != receipt.Id ||
            ticket.GameId != receipt.Blank.GameId || ticket.BlankId != receipt.Blank.BlankId || ticket.ImageVersion < 1)
            throw new IntakeException(IntakeError.RetakeConflict, "Retake is not valid for this receipt.");
        await CheckWindow(ticket, ct);
        await images.SaveAsync(receipt.Id, receipt.Blank.BlankId, command.Image, command.MediaType, ct);
        var now = await CheckWindow(ticket, ct);

        return await store.ExecuteAsync(async (s, token) =>
        {
            var saved = (await s.Operations.FindAsync(operation, token))?.Match(operation);
            if (saved?.ImageVersion is { } replay) return Reply(receipt, command.RetakeId, replay);
            if (await s.Images.LatestAsync(receiptId, token) != ticket.ImageVersion)
                throw new IntakeException(IntakeError.RetakeConflict, "This retake has already received a replacement.");
            var result = Reply(receipt, command.RetakeId, ticket.ImageVersion + 1);
            await s.Images.AddAsync(result, actorId, now, command.Image, command.MediaType, token);
            await s.Operations.SaveAsync(operation, new(receipt.Id, ImageVersion: result.ImageVersion), token);
            await s.Outbox.AddAsync(new RetakeImageStored(Guid.NewGuid().ToString(), now, receipt.Id,
                new(ticket.Id, ticket.CheckId, receipt.Id, ticket.BlankId, ticket.GameId, result.ImageVersion, receipt.AdmissionVersion))
            {
                OperationId = operation.WorkflowId,
                ProcessVersion = operation.WorkflowVersion
            }, token);
            return result;
        }, ct);
    }

    private async Task<DateTimeOffset> CheckWindow(RetakeAuthorization ticket, CancellationToken ct)
    {
        var game = await games.GetAsync(ticket.GameId, ct);
        var now = await clock.NowAsync(ct);
        if (game.Status is "completed" or "published" or "finished")
            throw new IntakeException(IntakeError.RetakeConflict, "Review is finalized.");
        if (!ticket.Approved && (ticket.RequiresApproval || now >= game.SubmissionDeadline))
            throw new IntakeException(IntakeError.RetakeApprovalRequired, "An administrator must approve this retake.");
        return now;
    }

    private static StoredRetake Reply(Receipt receipt, string id, int version) =>
        new(id, receipt.Id, version, $"/receipts/{receipt.Id}/image/versions/{version}", receipt.AcceptedAt);
}
