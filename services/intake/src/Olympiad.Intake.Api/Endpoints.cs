using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Api;

internal static class Endpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static void MapIntake(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "intake" }));
        app.MapPut("/intake-sessions/{gameId}", async (string gameId, HttpContext context, SynchronizeIntake useCase) =>
        {
            var request = await ReadAsync<AdmissionRequest>(context);
            ValidateVersion(request.AdmissionVersion);
            return Results.Ok(await useCase.ExecuteAsync(gameId, request.AdmissionVersion, Identity(context, request), context.RequestAborted));
        }).WithMetadata(new RequiredRoles("integration"));
        app.MapPost("/intake-sessions/{gameId}/close", async (string gameId, HttpContext context, CloseIntake useCase) =>
        {
            var request = await ReadAsync<AdmissionRequest>(context);
            ValidateVersion(request.AdmissionVersion);
            return Results.Ok(await useCase.ExecuteAsync(gameId, request.AdmissionVersion, Identity(context, request), context.RequestAborted));
        }).WithMetadata(new RequiredRoles("integration"));
        app.MapGet("/intake-sessions/{gameId}", async (string gameId, HttpContext context, GetIntakeSession useCase) =>
            Results.Ok(await useCase.ExecuteAsync(gameId, context.RequestAborted))).WithMetadata(new RequiredRoles("observer", "integration"));
        app.MapPost("/receipts", async (HttpContext context, AcceptBlank useCase) =>
        {
            var request = await ReadAsync<BlankRequest>(context);
            if (request.QrToken is null || request.ImageBase64 is null || request.ImageMediaType is null)
                throw new IntakeException(IntakeError.ValidationError, "All required text fields must be strings.");
            byte[] image;
            try { image = Convert.FromBase64String(request.ImageBase64); }
            catch (FormatException) { throw new IntakeException(IntakeError.InvalidImage, "Image is not valid base64."); }
            var result = await useCase.ExecuteAsync(new(request.QrToken, image, request.ImageMediaType, request.LegibilityConfirmed),
                Identity(context, request), context.RequestAborted);
            return Results.Json(ReceiptResponse.From(result.Receipt), statusCode: result.Replayed ? 200 : 201);
        }).WithMetadata(new RequiredRoles("observer"));
        app.MapGet("/receipts/{id}", async (string id, HttpContext context, GetReceipt useCase) =>
            Results.Ok(ReceiptResponse.From(await useCase.ExecuteAsync(id, context.RequestAborted))))
            .WithMetadata(new RequiredRoles("observer", "checker", "integration"));
    }
    private static async Task<T> ReadAsync<T>(HttpContext context) => await JsonSerializer.DeserializeAsync<T>(context.Request.Body, InputJson, context.RequestAborted)
        ?? throw new IntakeException(IntakeError.ValidationError, "A request body is required.");
    private static void ValidateVersion(int version)
    { if (version < 1) throw new IntakeException(IntakeError.ValidationError, "Admission version must be positive."); }
    private static OperationIdentity Identity<T>(HttpContext context, T request)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw new IntakeException(IntakeError.ValidationError, "An Idempotency-Key of at most 200 characters is required.");
        var actor = (Actor)context.Items[typeof(Actor)]!;
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, InputJson)));
        return new($"{actor.UserId}:{context.Request.Method}:{context.Request.Path}", key, fingerprint);
    }
    private sealed record AdmissionRequest { public required int AdmissionVersion { get; init; } }
    private sealed record BlankRequest
    {
        public required string QrToken { get; init; }
        public required string ImageBase64 { get; init; }
        public required string ImageMediaType { get; init; }
        public required bool LegibilityConfirmed { get; init; }
    }
    private sealed record ReceiptResponse(string Id, string BlankId, string GameId, int AdmissionVersion,
        string TeamId, string ImageRef, DateTimeOffset AcceptedAt, string TaskId, string TaskNumber)
    {
        public static ReceiptResponse From(Receipt receipt) => new(receipt.Id, receipt.Blank.BlankId, receipt.Blank.GameId,
            receipt.AdmissionVersion, receipt.Blank.TeamId, receipt.ImageRef, receipt.AcceptedAt, receipt.Blank.TaskId, receipt.Blank.TaskNumber);
    }
}
