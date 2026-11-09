using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Olympiad.Materials.Application;

namespace Olympiad.Materials.Infrastructure.Http;

public sealed class DependencyClient(HttpClient client)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) => await SendAsync<T>(new(HttpMethod.Get, path), cancellationToken);
    public async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken) => await SendAsync<T>(new(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) }, cancellationToken);
    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new NotFoundException("The dependency resource was not found.");
            if (!response.IsSuccessStatusCode) throw new DependencyUnavailableException($"Dependency returned {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken) ?? throw new DependencyUnavailableException("Dependency returned no JSON data.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new DependencyUnavailableException("Dependency request timed out."); }
        catch (HttpRequestException exception) { throw new DependencyUnavailableException("Dependency cannot be reached.", exception); }
        catch (JsonException exception) { throw new DependencyUnavailableException("Dependency returned malformed data.", exception); }
    }
}
