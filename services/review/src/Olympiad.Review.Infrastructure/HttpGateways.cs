using Olympiad.Review.Application;
using Olympiad.Review.Domain;
namespace Olympiad.Review.Infrastructure;
public sealed class HttpGameProfiles(DependencyHttp http) : IGameProfiles
{
    public async Task<GameProfile> Get(string id, CancellationToken cancellationToken)
    {
        var game = await http.Get<GameResponse>("/games/" + Uri.EscapeDataString(id), cancellationToken);
        return new(game.Id, game.AdmissionVersion, game.Status, game.SubmissionDeadline,
            game.AppealDeadline, game.LateSubmissionAllowed, game.Capabilities.AppealsAllowed);
    }
    private sealed record Capabilities(bool AppealsAllowed);
    private sealed record GameResponse(string Id, int AdmissionVersion, string Status,
        DateTimeOffset SubmissionDeadline, DateTimeOffset AppealDeadline, bool LateSubmissionAllowed, Capabilities Capabilities);
}
public sealed class HttpAcceptedReceipts(DependencyHttp http) : IAcceptedReceipts
{
    public Task<Receipt> Get(string id, CancellationToken cancellationToken) =>
        http.Get<Receipt>("/receipts/" + Uri.EscapeDataString(id), cancellationToken);
}
public sealed class HttpTaskAnswers(DependencyHttp http) : ITaskAnswers
{
    public async Task<string> Get(string taskId, CancellationToken cancellationToken)
    {
        var task = await http.Get<TaskResponse>("/tasks/" + Uri.EscapeDataString(taskId), cancellationToken);
        if (task.Id != taskId || task.ReferenceAnswer is null)
            throw new DomainFailure("DEPENDENCY_INVALID_RESPONSE", FailureKind.Dependency);
        return task.ReferenceAnswer;
    }
    private sealed record TaskResponse(string Id, string ReferenceAnswer);
}
public sealed class HttpIdentityProvider(DependencyHttp http) : IIdentityProvider
{
    public async Task<AuthenticatedActor?> Authenticate(string token, CancellationToken cancellationToken)
    {
        var response = await http.Post<IdentityResponse>("/auth/introspect", new { token }, cancellationToken);
        return response.Active ? new(response.UserId, response.Roles) : null;
    }
    private sealed record IdentityResponse(bool Active, string UserId, string[] Roles);
}
public sealed class HttpClock(DependencyHttp http) : IClock
{
    public async Task<DateTimeOffset> Now(CancellationToken cancellationToken) =>
        (await http.Get<ClockResponse>("/clock", cancellationToken)).Now;
    private sealed record ClockResponse(DateTimeOffset Now);
}
public sealed class SystemClock : IClock
{
    public Task<DateTimeOffset> Now(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(DateTimeOffset.UtcNow);
    }
}
public sealed class HttpEventPublisher(DependencyHttp http) : IEventPublisher
{
    public Task Publish(ReviewEvent message, CancellationToken cancellationToken) =>
        http.Publish("/events", EventSerialization.Serialize(message), cancellationToken);
}
