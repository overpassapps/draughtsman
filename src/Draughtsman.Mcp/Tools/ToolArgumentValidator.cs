using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Draughtsman.Mcp.Tools;

/// <summary>
/// The one place a tool call's arguments are checked against the tool's own JSON input schema before the SDK binds
/// them. The SDK's binder throws an ArgumentException for a missing or mistyped argument and then reports only
/// "An error occurred invoking 'tool'" (it hides non-McpException text on purpose), so an agent could not correct
/// its call. Checking here, from the schema the tool already publishes, covers every current and future tool with no
/// per-tool code. Messages carry only the tool name, the argument names and schema-derived type/enum names, never an
/// argument value, an exception message, a stack trace or a path.
/// </summary>
public static class ToolArgumentValidator
{
    /// <summary>Registers the validating filter; call once per MCP host, after <c>AddMcpServer()</c>.</summary>
    public static IMcpServerBuilder WithArgumentValidation(this IMcpServerBuilder builder)
    {
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            string? name = context.Params?.Name;
            McpServerTool? tool = null;
            if (name is not null && context.Server.ServerOptions.ToolCollection is { } tools)
            {
                tools.TryGetPrimitive(name, out tool);
            }

            if (tool is not null && Validate(tool.ProtocolTool.Name, tool.ProtocolTool.InputSchema, context.Params!.Arguments) is { } problem)
            {
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = problem }] };
            }

            return await next(context, cancellationToken);
        }));
    }

    /// <summary>Null when the arguments satisfy the schema; otherwise one sentence naming the first problem and how to fix it.</summary>
    public static string? Validate(string toolName, JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var required = new List<string>();
        if (inputSchema.TryGetProperty("required", out JsonElement requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
        {
            required.AddRange(requiredElement.EnumerateArray().Select(r => r.GetString()).OfType<string>());
        }

        JsonElement properties = default;
        bool hasProperties = inputSchema.TryGetProperty("properties", out properties) && properties.ValueKind == JsonValueKind.Object;

        foreach (string requiredName in required)
        {
            if (arguments is null || !arguments.TryGetValue(requiredName, out JsonElement supplied) || supplied.ValueKind == JsonValueKind.Null)
            {
                string type = hasProperties && properties.TryGetProperty(requiredName, out JsonElement p) ? Describe(p) : "value";
                return $"{toolName}: missing required argument '{requiredName}' ({type}). Required arguments: {string.Join(", ", required)}.";
            }
        }

        if (arguments is null || !hasProperties)
        {
            return null;
        }

        foreach ((string argName, JsonElement value) in arguments)
        {
            if (!properties.TryGetProperty(argName, out JsonElement schema) || schema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string[] types = TypesOf(schema);
            if (value.ValueKind == JsonValueKind.Null)
            {
                if (types.Length > 0 && !types.Contains("null"))
                {
                    return $"{toolName}: argument '{argName}' must be {Describe(schema)}; got null. Omit it to use the default.";
                }

                continue;
            }

            if (types.Length > 0 && !types.Any(t => Matches(t, value)))
            {
                return $"{toolName}: argument '{argName}' must be {Describe(schema)}; got {KindName(value)}.";
            }

            if (schema.TryGetProperty("enum", out JsonElement allowed) && allowed.ValueKind == JsonValueKind.Array
                && !allowed.EnumerateArray().Any(a => JsonElement.DeepEquals(a, value)))
            {
                return $"{toolName}: argument '{argName}' must be one of: {string.Join(", ", allowed.EnumerateArray().Select(a => a.ToString()))}.";
            }
        }

        return null;
    }

    private static string[] TypesOf(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
        {
            return [];
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => type.EnumerateArray().Select(t => t.GetString()).OfType<string>().ToArray(),
            _ => [],
        };
    }

    private static string Describe(JsonElement schema)
    {
        string[] types = TypesOf(schema).Where(t => t != "null").ToArray();
        string text = types.Length == 0 ? "a value" : "a " + string.Join(" or ", types);
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("enum", out JsonElement allowed) && allowed.ValueKind == JsonValueKind.Array)
        {
            text += " (one of: " + string.Join(", ", allowed.EnumerateArray().Select(a => a.ToString())) + ")";
        }

        return text;
    }

    private static bool Matches(string type, JsonElement value)
    {
        return type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && (value.TryGetInt64(out _) || (value.TryGetDouble(out double d) && d == Math.Floor(d) && double.IsFinite(d))),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true,
        };
    }

    private static string KindName(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            JsonValueKind.Array => "an array",
            JsonValueKind.Object => "an object",
            _ => "null",
        };
    }
}
