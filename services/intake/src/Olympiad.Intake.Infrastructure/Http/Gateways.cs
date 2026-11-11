using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Infrastructure.Http;

public sealed class HttpAdmissionGateway(HttpClient client) : IAdmissionGateway
{
    private readonly DependencyHttp http = new(client);
    public async Task<Admission> GetAsync(string gameId, CancellationToken ct)
    {
        var game = await http.GetAsync<GameDocument>($"games/{Uri.EscapeDataString(gameId)}", ct);
        if (game.Id != gameId || game.AdmissionVersion < 1)
            throw new IntakeException(IntakeError.DependencyUnavailable, "The game dependency returned inconsistent identity.");
        return new(game.Id, game.AdmissionVersion, game.Status, game.SubmissionDeadline, game.LateSubmissionAllowed);
    }
    private sealed record GameDocument(string Id, int AdmissionVersion, string Status, DateTimeOffset SubmissionDeadline, bool LateSubmissionAllowed);
}

public sealed class HttpTeamAdmissionGateway(HttpClient client) : ITeamAdmissionGateway
{
    private readonly DependencyHttp http = new(client);
    public async Task<TeamAdmission> GetAsync(string teamId, CancellationToken ct)
    {
        var team = await http.GetAsync<TeamDocument>($"teams/{Uri.EscapeDataString(teamId)}", ct);
        return new(team.Id, team.GameId, team.Admitted);
    }
    private sealed record TeamDocument(string Id, string GameId, bool Admitted);
}

public sealed class HttpBlankRegistry(HttpClient client) : IBlankRegistry
{
    private readonly DependencyHttp http = new(client);
    public async Task<BlankBinding> VerifyAsync(string qrToken, CancellationToken ct)
    {
        var blank = await http.PostAsync<BlankDocument>("blank-verifications", new { qrToken }, ct, IntakeError.InvalidQr, Guid.NewGuid().ToString());
        return new(blank.Id, blank.GameId, blank.TeamId, blank.TaskId, blank.TaskNumber);
    }
    private sealed record BlankDocument(string Id, string GameId, string TeamId, string TaskId, string TaskNumber);
}

public sealed class HttpIdentityProvider(HttpClient client) : IIdentityProvider
{
    private readonly DependencyHttp http = new(client);
    public async Task<Actor?> IntrospectAsync(string token, CancellationToken ct)
    {
        var identity = await http.PostAsync<IdentityDocument>("auth/introspect", new { token }, ct);
        return identity.Active && !string.IsNullOrWhiteSpace(identity.UserId)
            ? new(identity.UserId, identity.Roles.ToHashSet(StringComparer.Ordinal)) : null;
    }
    private sealed record IdentityDocument(bool Active, string UserId, string[] Roles);
}

public sealed class HttpImageStorage(HttpClient client) : IImageStorage, IImageReader
{
    public Task<byte[]> ReadAsync(ImageReference image, CancellationToken ct)
    {
        if (!Uri.TryCreate(client.BaseAddress, image.DownloadRef, out var address) ||
            address.Authority != client.BaseAddress!.Authority)
            throw new IntakeException(IntakeError.StorageUnavailable, "Image dependency returned an invalid reference.");
        return client.GetByteArrayAsync(address, ct);
    }
    private readonly DependencyHttp http = new(client);
    public async Task<string> SaveAsync(string receiptId, string blankId, ReadOnlyMemory<byte> image, string mediaType, CancellationToken ct)
    {
        try
        {
            var result = await http.PostAsync<ImageDocument>("images", new { receiptId, blankId, dataBase64 = Convert.ToBase64String(image.Span), mediaType }, ct, idempotencyKey: receiptId);
            if (string.IsNullOrWhiteSpace(result.ImageRef))
                throw new IntakeException(IntakeError.StorageUnavailable, "Image storage returned no durable reference.");
            return result.ImageRef;
        }
        catch (IntakeException exception) when (exception.Error == IntakeError.DependencyUnavailable)
        { throw new IntakeException(IntakeError.StorageUnavailable, "The verified image could not be durably stored."); }
    }
    private sealed record ImageDocument(string ImageRef);
}

public sealed class HttpEventPublisher(HttpClient client, Olympiad.Storage.S3.S3Objects? images = null) : IEventPublisher
{
    private readonly DependencyHttp http = new(client);
    public Task PublishAsync(IntakeEvent message, CancellationToken ct) => message is UploadAbandoned abandoned
        ? images?.DeleteOwnerAsync(abandoned.AggregateId,ct) ?? Task.CompletedTask
        : http.PublishAsync(Persistence.EventSerialization.Serialize(message), message.EventId, ct);
}

public sealed class SystemClock : IClock
{
    public ValueTask<DateTimeOffset> NowAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(DateTimeOffset.UtcNow); }
}

public sealed class HttpContractClock(HttpClient client) : IClock
{
    private readonly DependencyHttp http = new(client);
    public async ValueTask<DateTimeOffset> NowAsync(CancellationToken ct) => (await http.GetAsync<ClockDocument>("clock", ct)).Now;
    private sealed record ClockDocument(DateTimeOffset Now);
}
