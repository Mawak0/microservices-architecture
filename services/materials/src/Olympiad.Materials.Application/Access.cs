namespace Olympiad.Materials.Application;

public enum MaterialPermission { Issue, Prepare, Print, Read, ReadKit, Verify }
public sealed class Actor(string id, IEnumerable<string> roles)
{
    public string Id { get; } = id;
    private readonly HashSet<string> roles = new(roles, StringComparer.Ordinal);
    public IReadOnlyCollection<string> RoleNames => roles.ToArray();
    public void Require(MaterialPermission permission)
    {
        if (roles.Contains("admin")) return;
        if (permission is MaterialPermission.Read or MaterialPermission.ReadKit or MaterialPermission.Print or MaterialPermission.Verify && roles.Contains("observer")) return;
        if (permission is MaterialPermission.Verify && roles.Contains("integration")) return;
        if (permission is MaterialPermission.Read && roles.Contains("integration")) return;
        throw new AccessDeniedException();
    }
}
public sealed class AccessDeniedException() : Exception("The authenticated user does not have this capability.");
public sealed class DependencyUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class NotFoundException(string message) : Exception(message);
public sealed record CommandContext(Actor Actor, string IdempotencyKey);
public interface IIdentityGateway { Task<Actor?> ResolveAsync(string token, CancellationToken cancellationToken); }
