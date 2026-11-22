using Olympiad.Intake.Application;

namespace Olympiad.Intake.Infrastructure.Http;

public sealed class HttpRetakeAuthorization(HttpClient client) : IRetakeAuthorization
{
    private readonly DependencyHttp http = new(client);

    public Task<RetakeAuthorization> GetAsync(string checkId, string requestId, CancellationToken ct) =>
        http.GetAsync<RetakeAuthorization>(
            $"integrations/retakes/{Uri.EscapeDataString(checkId)}/{Uri.EscapeDataString(requestId)}", ct);
}
