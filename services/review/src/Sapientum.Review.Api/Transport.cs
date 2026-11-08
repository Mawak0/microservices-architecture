using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sapientum.Review.Application;
using Sapientum.Review.Domain;
namespace Sapientum.Review.Api;

internal static class Transport
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };
    public static async Task<T> Body<T>(HttpContext context)
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync(context.RequestAborted); context.Request.Body.Position = 0;
        return JsonSerializer.Deserialize<T>(text, Json)
            ?? throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
    }
    public static async Task<CommandIdentity> Identity(HttpContext context)
    {
        var actor = (AuthenticatedActor)context.Items["actor"]!;
        var key = Required.Text(context.Request.Headers["Idempotency-Key"].FirstOrDefault());
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted); context.Request.Body.Position = 0;
        var token = context.Request.Headers["Assignment-Token"].FirstOrDefault() ?? "";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body + "\n" + token)));
        return new(actor.Id, context.Request.Method + " " + context.Request.Path, key, hash);
    }
    public static string Token(HttpContext context) =>
        Required.Text(context.Request.Headers["Assignment-Token"].FirstOrDefault());
    public static bool MayReleaseOtherOwner(HttpContext context) =>
        ((AuthenticatedActor)context.Items["actor"]!).Roles.Contains("admin");
    public static IResult Reply<T>(Outcome<T> result) => result.Value is null
        ? Results.NoContent() : Results.Json(result.Value, statusCode: result.Created ? 201 : 200);
    public static string GameQuery(HttpContext context) => Required.Text(context.Request.Query["gameId"].FirstOrDefault());
}
internal sealed record Roles(params string[] Allowed);
internal sealed record GameRequest
{
    public required string GameId { get; init; }
}
internal sealed record VerdictRequest
{
    public required string Verdict { get; init; }
}
internal sealed record CorrectionRequest
{
    public required string Verdict { get; init; }
    public required string Reason { get; init; }
}
internal sealed record AppealRequest
{
    public required string CheckId { get; init; }
    public required string Reason { get; init; }
}
internal sealed record ResolutionRequest
{
    public required string Outcome { get; init; }
    public required string Reason { get; init; }
    public string? Verdict { get; init; }
}
internal sealed record LimitRequest
{
    public required int Limit { get; init; }
}
internal sealed record Incoming<T>
{
    public required string EventId { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string AggregateId { get; init; }
    public required T Data { get; init; }
}
internal sealed record ReceiptData
{
    public required string ReceiptId { get; init; }
    public required string BlankId { get; init; }
    public required string GameId { get; init; }
    public required int AdmissionVersion { get; init; }
    public required string TeamId { get; init; }
    public required string TaskId { get; init; }
    public required string TaskNumber { get; init; }
    public required string ImageRef { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public Receipt Receipt() => new(ReceiptId, BlankId, GameId, AdmissionVersion,
        TeamId, TaskId, TaskNumber, ImageRef, AcceptedAt);
}
internal sealed record ClosureData
{
    public required string GameId { get; init; }
    public required int AdmissionVersion { get; init; }
    public required string[] ReceiptIds { get; init; }
}
