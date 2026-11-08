using Sapientum.Intake.Application;

namespace Sapientum.Intake.Api;

internal sealed record RequiredRoles(params string[] Roles);

internal sealed class AuthorizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IIdentityProvider identities)
    {
        var requirement = context.GetEndpoint()?.Metadata.GetMetadata<RequiredRoles>();
        if (requirement is null) { await next(context); return; }
        var header = context.Request.Headers.Authorization.ToString();
        Actor? actor = null;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > 7)
            actor = await identities.IntrospectAsync(header[7..], context.RequestAborted);
        if (actor is null)
        { await Errors.WriteAsync(context, 401, "UNAUTHENTICATED", "Valid bearer authentication is required."); return; }
        if (!actor.Roles.Contains("admin") && !requirement.Roles.Any(actor.Roles.Contains))
        { await Errors.WriteAsync(context, 403, "FORBIDDEN", "The authenticated roles do not authorize this operation."); return; }
        context.Items[typeof(Actor)] = actor;
        await next(context);
    }
}
