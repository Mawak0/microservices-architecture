using System.Text.Json;
using Olympiad.Review.Application;
using Olympiad.Review.Domain;
namespace Olympiad.Review.Api;
internal sealed class RequestMiddleware(RequestDelegate next, ILogger<RequestMiddleware> logger)
{
    public async Task Invoke(HttpContext context, IIdentityProvider identities)
    {
        try
        {
            var roles = context.GetEndpoint()?.Metadata.GetMetadata<Roles>();
            if (roles is not null)
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                { await Error(context, 401, "UNAUTHENTICATED"); return; }
                var actor = await identities.Authenticate(authorization[7..], context.RequestAborted);
                if (actor is null) { await Error(context, 401, "UNAUTHENTICATED"); return; }
                if (!actor.Roles.Contains("admin") && !roles.Allowed.Any(actor.Roles.Contains))
                { await Error(context, 403, "FORBIDDEN"); return; }
                context.Items["actor"] = actor;
            }
            await next(context);
        }
        catch (DomainFailure failure)
        {
            await Error(context, failure.Kind switch
            {
                FailureKind.Validation => 422,
                FailureKind.Missing => 404,
                FailureKind.Dependency => 503,
                _ => 409
            }, failure.Code);
        }
        catch (JsonException) { await Error(context, 422, "VALIDATION_ERROR"); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled service failure");
            await Error(context, 500, "INTERNAL_ERROR");
        }
    }
    private static Task Error(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { code, message = code }, context.RequestAborted);
    }
}
