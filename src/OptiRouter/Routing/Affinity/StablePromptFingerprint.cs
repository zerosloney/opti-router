using System.Security.Cryptography;
using System.Text.Json;
using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>Creates a deterministic SHA-256 fingerprint from stable prompt material only.</summary>
public static class StablePromptFingerprint
{
    private static readonly string[] StableExtensionFields =
        ["functions", "parallel_tool_calls", "response_format", "tool_choice", "tools"];

    public static string? Compute(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var messages = (request.Messages ?? Array.Empty<ChatMessage>())
            .OfType<ChatMessage>()
            .ToList();
        bool hasSystem = messages.Any(m =>
            m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(m.GetText()));
        bool hasExtension = request.ExtensionData is not null
            && StableExtensionFields.Any(request.ExtensionData.ContainsKey);
        if (!hasSystem && !hasExtension) return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("system_messages");
            writer.WriteStartArray();
            foreach (var message in messages.Where(m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase)))
                WriteCanonical(writer, message.Content);
            writer.WriteEndArray();

            writer.WritePropertyName("stable_fields");
            writer.WriteStartObject();
            foreach (string field in StableExtensionFields)
            {
                if (request.ExtensionData?.TryGetValue(field, out var value) == true)
                {
                    writer.WritePropertyName(field);
                    WriteCanonical(writer, value);
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement? value)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
            return;
        }
        WriteCanonical(writer, value.Value);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
