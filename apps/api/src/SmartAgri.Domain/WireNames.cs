using System.Reflection;
using System.Text.Json.Serialization;

namespace SmartAgri.Domain;

/// <summary>
/// The external ("wire") name of each member of an enum, taken from its
/// <see cref="JsonStringEnumMemberNameAttribute"/> — the same names the JSON serializer
/// writes. Lets the database store those names too, so the frontend, the API and the
/// database share one spelling per value.
/// </summary>
public static class WireNames<TEnum>
    where TEnum : struct, Enum
{
    private static readonly IReadOnlyDictionary<TEnum, string> NamesByValue = BuildNames();

    private static readonly IReadOnlyDictionary<string, TEnum> ValuesByName =
        NamesByValue.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>Every declared member's wire name, in declaration order.</summary>
    public static IReadOnlyCollection<string> All { get; } = [.. NamesByValue.Values];

    public static string ToWire(TEnum value) =>
        NamesByValue.TryGetValue(value, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Not a declared {typeof(TEnum).Name} value.");

    public static TEnum Parse(string name) =>
        ValuesByName.TryGetValue(name, out var value)
            ? value
            : throw new FormatException($"'{name}' is not a known {typeof(TEnum).Name} name.");

    private static Dictionary<TEnum, string> BuildNames()
    {
        var names = new Dictionary<TEnum, string>();
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()
                ?? throw new InvalidOperationException(
                    $"{typeof(TEnum).Name}.{field.Name} has no [JsonStringEnumMemberName]; every member needs an explicit wire name.");
            names.Add((TEnum)field.GetValue(null)!, attribute.Name);
        }

        return names;
    }
}
