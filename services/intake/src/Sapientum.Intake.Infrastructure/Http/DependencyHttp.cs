using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sapientum.Intake.Domain;

namespace Sapientum.Intake.Infrastructure.Http;

internal sealed class DependencyHttp(HttpClient client)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => client.GetAsync(path, ct));
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new IntakeException(IntakeError.NotFound, "The requested dependency resource was not found.");
        return await ReadAsync<T>(response, ct);
    }
    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct, IntakeError? rejected = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await SendAsync(() => client.SendAsync(request, ct));
        if (rejected is not null && response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound)
            throw new IntakeException(rejected.Value, "The dependency rejected the request.");
        return await ReadAsync<T>(response, ct);
    }
    public async Task PublishAsync(string json, string eventId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "events")
        { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", eventId);
        using var response = await SendAsync(() => client.SendAsync(request, ct));
        if (!response.IsSuccessStatusCode) throw new IntakeException(IntakeError.DependencyUnavailable, "Event transport is unavailable.");
    }
    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try { return await send(); }
        catch (HttpRequestException) { throw new IntakeException(IntakeError.DependencyUnavailable, "An external dependency is unavailable."); }
    }
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new IntakeException(IntakeError.DependencyUnavailable, "An external dependency is unavailable.");
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new IntakeException(IntakeError.DependencyUnavailable, "The dependency returned an empty result.");
        }
        catch (JsonException) { throw new IntakeException(IntakeError.DependencyUnavailable, "The dependency returned an invalid result."); }
    }
}
