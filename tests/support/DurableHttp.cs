namespace Olympiad.Testing;

/// <summary>Waits for the result of an asynchronous command by repeating the same idempotent request.</summary>
internal static class DurableHttp
{
    public static async Task<HttpResponseMessage> Settled(Func<Task<HttpResponseMessage>> send)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var response = await send();
            var pending = response.StatusCode == System.Net.HttpStatusCode.Accepted &&
                response.Headers.TryGetValues("Operation-Status", out var values) &&
                values.Single() is "Accepted" or "Running";
            if (!pending || DateTimeOffset.UtcNow >= deadline) return response;
            response.Dispose(); await Task.Delay(100);
        }
    }
}
