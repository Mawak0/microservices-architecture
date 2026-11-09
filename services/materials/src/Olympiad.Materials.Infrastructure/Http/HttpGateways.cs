using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;
using System.Text.Json;

namespace Olympiad.Materials.Infrastructure.Http;

public sealed class HttpIdentityGateway(DependencyClient client) : IIdentityGateway
{
    public async Task<Actor?> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync<Introspection>("auth/introspect", new { token }, cancellationToken);
        return response.Active && !string.IsNullOrWhiteSpace(response.UserId) ? new(response.UserId, response.Roles) : null;
    }
    private sealed record Introspection(bool Active, string UserId, string[] Roles);
}
public sealed class HttpGameMaterialsGateway(DependencyClient client) : IGameMaterialsGateway
{
    public async Task<GameMaterials> GetAsync(string id, CancellationToken cancellationToken)
    {
        var game = await client.GetAsync<GameDto>($"games/{Uri.EscapeDataString(id)}", cancellationToken);
        if (game.Id != id) throw new DependencyUnavailableException("The game dependency returned a different identity.");
        return new(game.Id, game.Capabilities.ModifiersAllowed, game.MaterialSettings.AnswerBlankCount,
            game.MaterialSettings.TaskCardCount, game.MaterialSettings.TaskCardsShared, game.MaterialSettings.TemplateKey,
            game.TaskAssignments.Select(t => new TaskAssignment(t.TaskId, t.TaskNumber)), game.MaterialSettings.TemplateParameters.ToDictionary(p => p.Key, p => ParseParameter(p.Value)));
    }
    internal static TemplateValue ParseParameter(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => new(TemplateValueKind.Text, element.GetString()),
        JsonValueKind.Number => new(TemplateValueKind.Number, Number: element.GetDecimal()),
        JsonValueKind.True => new(TemplateValueKind.Boolean, Boolean: true),
        JsonValueKind.False => new(TemplateValueKind.Boolean, Boolean: false),
        JsonValueKind.Null => new(TemplateValueKind.Null),
        _ => throw new DependencyUnavailableException("Template parameters must be scalar values.")
    };
    private sealed record GameDto(string Id, CapabilitiesDto Capabilities, SettingsDto MaterialSettings, AssignmentDto[] TaskAssignments);
    private sealed record CapabilitiesDto(bool ModifiersAllowed);
    private sealed record SettingsDto(int AnswerBlankCount, int TaskCardCount, bool TaskCardsShared, string TemplateKey, Dictionary<string, JsonElement> TemplateParameters);
    private sealed record AssignmentDto(string TaskId, string TaskNumber);
}
public sealed class HttpTeamAdmissionGateway(DependencyClient client) : ITeamAdmissionGateway
{
    public async Task<TeamAdmission> GetAsync(string id, CancellationToken cancellationToken)
    {
        var team = await client.GetAsync<TeamDto>($"teams/{Uri.EscapeDataString(id)}", cancellationToken);
        if (team.Id != id) throw new DependencyUnavailableException("The team dependency returned a different identity.");
        return new(team.Id, team.GameId, team.Admitted, team.Name ?? team.Id);
    }
    private sealed record TeamDto(string Id, string GameId, bool Admitted, string? Name);
}
public sealed class HttpTaskStatementGateway(DependencyClient client) : ITaskStatementGateway
{
    public async Task<string> GetStatementAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = await client.GetAsync<StatementDto>($"tasks/{Uri.EscapeDataString(taskId)}", cancellationToken);
        if (task.Id != taskId) throw new DependencyUnavailableException("The task catalog returned a different immutable identity.");
        return task.Statement;
    }
    // referenceAnswer deliberately has no destination in this adapter projection.
    private sealed record StatementDto(string Id, string Statement);
}
public sealed class SystemClock : IClock
{
    public Task<DateTimeOffset> UtcNowAsync(CancellationToken cancellationToken) => Task.FromResult(DateTimeOffset.UtcNow);
}
public sealed class HttpContractClock(DependencyClient client) : IClock
{
    public async Task<DateTimeOffset> UtcNowAsync(CancellationToken cancellationToken) => (await client.GetAsync<ClockDto>("clock", cancellationToken)).Now;
    private sealed record ClockDto(DateTimeOffset Now);
}
public sealed class HttpEventPublisher(DependencyClient client) : IEventPublisher
{
    public async Task PublishAsync(MaterialEvent message, CancellationToken cancellationToken)
    {
        object data = message.Type switch
        {
            "KitPrepared" => new { message.GameId, message.TeamId, message.KitId },
            "BlanksIssued" => new { message.GameId, message.TeamId, message.BlankIds },
            "PrintPdfPrepared" => new { message.GameId, message.TeamId, message.PrintJobId, message.ArtifactUrl, message.TemplateKey },
            _ => throw new InvalidOperationException("Unsupported material event.")
        };
        await client.PostAsync<JsonElement>("events", new { message.EventId, message.Type, message.OccurredAt, message.AggregateId, data }, cancellationToken);
    }
}
