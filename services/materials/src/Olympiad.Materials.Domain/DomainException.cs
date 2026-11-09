namespace Olympiad.Materials.Domain;

/// <summary>A stable business failure; transport status mapping belongs to the API adapter.</summary>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class Guard
{
    public static string Id(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException("VALIDATION_ERROR", $"{name} is required.");
        return value;
    }
}
