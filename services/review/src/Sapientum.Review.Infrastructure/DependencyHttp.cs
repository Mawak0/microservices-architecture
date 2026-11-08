using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sapientum.Review.Domain;
namespace Sapientum.Review.Infrastructure;
public sealed class DependencyHttp(HttpClient client, Uri baseAddress, string? accessToken)
{
    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, new Uri(baseAddress, path.TrimStart('/'))) { Content = content };
        if (!string.IsNullOrEmpty(accessToken)) request.Headers.Authorization = new("Bearer", accessToken);
        return request;
    }
    public async Task<T> Get<T>(string path, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path, null);
        return await Send<T>(request, cancellationToken);
    }
    public async Task<T> Post<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, path, JsonContent.Create(body));
        return await Send<T>(request, cancellationToken);
    }
    public async Task Publish(string path, string body, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, path, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new DomainFailure("EVENT_TRANSPORT_UNAVAILABLE", FailureKind.Dependency);
    }
    private async Task<T> Send<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new DomainFailure("NOT_FOUND", FailureKind.Missing);
            if (!response.IsSuccessStatusCode) throw new DomainFailure("DEPENDENCY_UNAVAILABLE", FailureKind.Dependency);
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
                ?? throw new DomainFailure("DEPENDENCY_INVALID_RESPONSE", FailureKind.Dependency);
        }
        catch (HttpRequestException) { throw new DomainFailure("DEPENDENCY_UNAVAILABLE", FailureKind.Dependency); }
        catch (JsonException) { throw new DomainFailure("DEPENDENCY_INVALID_RESPONSE", FailureKind.Dependency); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DomainFailure("DEPENDENCY_UNAVAILABLE", FailureKind.Dependency); }
    }
}
