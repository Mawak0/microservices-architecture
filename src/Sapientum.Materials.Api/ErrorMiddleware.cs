using System.Text.Json;
using Sapientum.Materials.Application;
using Sapientum.Materials.Domain;

namespace Sapientum.Materials.Api;

internal sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var (status, code, message) = exception switch
            {
                UnauthenticatedException => (401, "UNAUTHENTICATED", exception.Message),
                AccessDeniedException => (403, "FORBIDDEN", exception.Message),
                NotFoundException => (404, "NOT_FOUND", exception.Message),
                DependencyUnavailableException => (503, "DEPENDENCY_UNAVAILABLE", exception.Message),
                JsonException => (422, "VALIDATION_ERROR", "The JSON body does not match the contract."),
                BadHttpRequestException => (422, "VALIDATION_ERROR", "The HTTP body cannot be read."),
                DomainException domain => (domain.Code is "TEAM_NOT_ADMITTED" or "IDEMPOTENCY_KEY_REUSED" or "PRINT_ASSIGNMENT_STALE" ? 409 : 422, domain.Code, domain.Message),
                _ => (500, "INTERNAL_ERROR", "The operation could not be completed.")
            };
            if (status == 500) logger.LogError(exception, "Unhandled material-service error");
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { code, message }, context.RequestAborted);
        }
    }
}
