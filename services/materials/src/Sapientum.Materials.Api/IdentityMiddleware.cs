using Sapientum.Materials.Application;

namespace Sapientum.Materials.Api;

internal sealed class IdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IIdentityGateway identities)
    {
        if (context.Request.Path == "/health") { await next(context); return; }
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new UnauthenticatedException();
        var actor = await identities.ResolveAsync(header[7..].Trim(), context.RequestAborted) ?? throw new UnauthenticatedException();
        context.Items[typeof(Actor)] = actor;
        await next(context);
    }
}
internal sealed class UnauthenticatedException() : Exception("A valid Bearer identity is required.");
internal static class HttpIdentity
{
    public static Actor Actor(this HttpContext context) => context.Items[typeof(Actor)] as Actor ?? throw new UnauthenticatedException();
    public static CommandContext Command(this HttpContext context) => new(context.Actor(), context.Request.Headers["Idempotency-Key"].ToString());
}
