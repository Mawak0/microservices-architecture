using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Olympiad.Workflows.Infrastructure;

public static class OperationGame
{
    public static string? FromRequest(HttpContext context, JsonElement? body) =>
        context.Request.RouteValues["gameId"] as string ?? Identifier(body, "gameId");

    public static string? Identifier(JsonElement? body, string name)
    {
        if (body is not { ValueKind: JsonValueKind.Object } arguments) return null;

        string? value = null;
        foreach (var property in arguments.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (value is not null || property.Value.ValueKind != JsonValueKind.String) return null;
            value = property.Value.GetString();
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
