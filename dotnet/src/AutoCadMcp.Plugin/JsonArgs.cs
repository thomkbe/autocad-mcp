using System.Text.Json.Nodes;

namespace AutoCadMcp.Plugin;

/// <summary>Typed access to request arguments. JSON null counts as missing.</summary>
internal static class JsonArgs
{
    public static double RequireDouble(this JsonObject args, string name) =>
        args.OptionalDouble(name) ?? throw Missing(name);

    public static double? OptionalDouble(this JsonObject args, string name)
    {
        var value = Value(args, name);
        if (value is null)
            return null;
        return value.TryGetValue(out double number) && double.IsFinite(number)
            ? number
            : throw new CommandException($"Argument '{name}' must be a number.");
    }

    public static int? OptionalInt(this JsonObject args, string name)
    {
        var value = Value(args, name);
        if (value is null)
            return null;
        return value.TryGetValue(out int number)
            ? number
            : throw new CommandException($"Argument '{name}' must be an integer.");
    }

    public static bool? OptionalBool(this JsonObject args, string name)
    {
        var value = Value(args, name);
        if (value is null)
            return null;
        return value.TryGetValue(out bool flag)
            ? flag
            : throw new CommandException($"Argument '{name}' must be true or false.");
    }

    public static string RequireString(this JsonObject args, string name) =>
        args.OptionalString(name) ?? throw Missing(name);

    public static string? OptionalString(this JsonObject args, string name)
    {
        var value = Value(args, name);
        if (value is null)
            return null;
        if (!value.TryGetValue(out string? text))
            throw new CommandException($"Argument '{name}' must be a string.");
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static IReadOnlyList<string> RequireStringArray(this JsonObject args, string name, bool allowEmpty = false)
    {
        if (args[name] is not JsonArray array || (array.Count == 0 && !allowEmpty))
            throw new CommandException($"Argument '{name}' must be a {(allowEmpty ? "" : "non-empty ")}array of strings.");

        return array.Select(item => item is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0
                ? s
                : throw new CommandException($"Argument '{name}' must contain only non-empty strings."))
            .ToList();
    }

    private static JsonValue? Value(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue value => value,
        _ => throw new CommandException($"Argument '{name}' must be a single value."),
    };

    private static CommandException Missing(string name) => new($"Missing required argument '{name}'.");
}
