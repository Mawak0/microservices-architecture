using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Infrastructure.Security;

/// <summary>Versioned authenticated identity. Verification additionally requires an exact registry match.</summary>
public sealed class HmacBlankTokenProtector : IBlankTokenProtector
{
    private readonly byte[] key;
    public HmacBlankTokenProtector(byte[] key)
    {
        if (key.Length < 32) throw new ArgumentException("Use at least 256 bits of random key material.", nameof(key));
        this.key = key.ToArray();
    }
    public string Protect(BlankIdentity identity)
    {
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(TokenPayload.From(identity)));
        var protectedPart = $"sp1.{payload}";
        return $"{protectedPart}.{Encode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(protectedPart)))}";
    }
    public BlankIdentity? Unprotect(string token)
    {
        if (token.Length > 8192) return null;
        var pieces = token.Split('.');
        if (pieces.Length != 3 || pieces[0] != "sp1") return null;
        try
        {
            var supplied = Decode(pieces[2]);
            var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"sp1.{pieces[1]}"));
            if (!CryptographicOperations.FixedTimeEquals(supplied, expected)) return null;
            return JsonSerializer.Deserialize<TokenPayload>(Decode(pieces[1]))?.Restore();
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
        catch (DomainException) { return null; }
    }
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
    }
    private sealed record TokenPayload(string Id, long Number, string Game, string Team, string Task, string TaskNumber, string? ModifierKind, decimal? ModifierValue)
    {
        public static TokenPayload From(BlankIdentity identity) => new(identity.Id, identity.Number, identity.GameId, identity.TeamId,
            identity.TaskId, identity.TaskNumber, identity.Modifier?.Kind.ToString(), identity.Modifier?.Value);
        public BlankIdentity Restore() => new(Id, Number, Game, Team, Task, TaskNumber,
            ModifierKind is null ? null : new ScoreModifier(Enum.Parse<Domain.ModifierKind>(ModifierKind), ModifierValue ?? throw new FormatException("Missing modifier value.")));
    }
}
