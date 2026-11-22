using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Olympiad.Http;

public static class PrivateResponses
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void UsePrivateResponses(this WebApplication app) => app.Use(async (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        await next(context);
    });

    // Invoke after authorization and resource lookup, including on conditional requests.
    public static IResult Revalidate<T>(HttpContext context, string actorId, T data)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(actorId));
        hash.AppendData([0]);
        hash.AppendData(body);
        var current = new EntityTagHeaderValue($"\"{Convert.ToHexString(hash.GetHashAndReset())}\"");
        context.Response.Headers.CacheControl = "private, no-cache";
        context.Response.Headers.ETag = current.ToString();
        context.Response.Headers.AppendCommaSeparatedValues(HeaderNames.Vary, HeaderNames.Authorization, HeaderNames.Cookie);
        var validators = context.Request.GetTypedHeaders().IfNoneMatch;
        return validators.Any(tag => tag == EntityTagHeaderValue.Any || tag.Compare(current, useStrongComparison: false))
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Bytes(body, "application/json; charset=utf-8");
    }

    public static bool TryPage(HttpRequest request, out int offset, out int limit)
    {
        offset = 0;
        limit = 30;
        var query = request.Query;
        return (!query.ContainsKey("offset") || int.TryParse(query["offset"], out offset) && offset is >= 0 and <= 100_000)
            && (!query.ContainsKey("limit") || int.TryParse(query["limit"], out limit) && limit is >= 1 and <= 100);
    }
}
