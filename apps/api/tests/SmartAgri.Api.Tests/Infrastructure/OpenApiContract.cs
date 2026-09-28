using System.Linq;
using System.Text.Json;
using Shouldly;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// Checks a real endpoint response's JSON against <c>apps/api/openapi/v1.json</c>: every key the
/// document marks <c>required</c> for a schema must actually be present in the JSON, recursively
/// through nested objects, array items and <c>oneOf</c> branches (issue #106 — an earlier version
/// of <see cref="Api.Chat.ChatMessageView"/>/<see cref="Api.Chat.ChatReplyView"/> used
/// <c>JsonIgnore(WhenWritingNull)</c> to omit some of these "required" keys when null, so the
/// document and the wire disagreed; see <c>ChatEndpoints.cs</c>'s class-level note). This is a
/// structural presence check, not full JSON Schema validation (types, enums, formats etc. are
/// already covered by the many typed assertions elsewhere in these tests) — it exists to catch
/// exactly the omitted-required-key class of bug, cheaply, without re-validating everything else.
/// </summary>
public static class OpenApiContract
{
    private static readonly Lazy<JsonElement> Schemas = new(() =>
    {
        var path = Path.Combine(RepositoryRoot(), "apps", "api", "openapi", "v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("components").GetProperty("schemas").Clone();
    });

    /// <summary>Asserts every property <paramref name="schemaName"/> (and everything it
    /// references, recursively) marks <c>required</c> is present in <paramref name="body"/>.</summary>
    public static void AssertKeysMatchSchema(JsonElement body, string schemaName)
    {
        var schema = Schemas.Value.GetProperty(schemaName);
        AssertMatches(body, schema, schemaName);
    }

    private static void AssertMatches(JsonElement value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("$ref", out var refProp))
        {
            AssertMatches(value, ResolveRef(refProp), path);
            return;
        }

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            // ChatMessageView.reply-style "T | null": a null value only satisfies the branch
            // {"type": "null"}, so there is nothing further to check; otherwise recurse into
            // whichever $ref branch is there (this codebase's oneOf usage is always exactly
            // "null or one $ref", never a real multi-type union — see ChatEndpoints.cs's note).
            if (value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            foreach (var branch in oneOf.EnumerateArray())
            {
                if (branch.TryGetProperty("$ref", out var branchRef))
                {
                    AssertMatches(value, ResolveRef(branchRef), path);
                    return;
                }
            }

            return;
        }

        if (!schema.TryGetProperty("type", out var typeProp))
        {
            return;
        }

        // A nullable scalar (e.g. ChatReplyView.notice) is OpenAPI 3.1 JSON Schema style
        // `"type": ["null", "string"]`, not `oneOf` — only nullable $refs go through the oneOf
        // branch above. Either way a null value needs no further checking.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        var type = typeProp.ValueKind == JsonValueKind.Array
            ? typeProp.EnumerateArray().Select(t => t.GetString()).FirstOrDefault(t => t != "null")
            : typeProp.GetString();

        switch (type)
        {
            case "object":
                AssertObjectMatches(value, schema, path);
                break;
            case "array":
                AssertArrayMatches(value, schema, path);
                break;
        }
    }

    private static void AssertObjectMatches(JsonElement value, JsonElement schema, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            // A schema-typed "object" slot holding something else (e.g. an unexpected null) is
            // its own bug, but not the one this check exists for; the required-key check below
            // is what matters, and it needs an object to check properties against.
            return;
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var key in required.EnumerateArray())
            {
                var name = key.GetString()!;
                value.TryGetProperty(name, out _).ShouldBeTrue(
                    $"{path}.{name} is required by the OpenAPI schema but missing from the JSON response.");
            }
        }

        if (!schema.TryGetProperty("properties", out var properties))
        {
            return;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (value.TryGetProperty(property.Name, out var child))
            {
                AssertMatches(child, property.Value, $"{path}.{property.Name}");
            }
        }
    }

    private static void AssertArrayMatches(JsonElement value, JsonElement schema, string path)
    {
        if (value.ValueKind != JsonValueKind.Array || !schema.TryGetProperty("items", out var items))
        {
            return;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            AssertMatches(item, items, $"{path}[{index}]");
            index++;
        }
    }

    private static JsonElement ResolveRef(JsonElement refProp)
    {
        var pointer = refProp.GetString()!; // "#/components/schemas/ChatMessageView"
        var name = pointer[(pointer.LastIndexOf('/') + 1)..];
        return Schemas.Value.GetProperty(name);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "nx.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (nx.json) above " + AppContext.BaseDirectory);
    }
}
