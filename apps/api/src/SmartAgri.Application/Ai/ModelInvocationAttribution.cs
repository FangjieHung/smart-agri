using Microsoft.Extensions.AI;
using SmartAgri.Domain.Ai;

namespace SmartAgri.Application.Ai;

/// <summary>
/// Who and what a model call is for, carried to the recording middleware in the call's
/// options (<see cref="EmbeddingGenerationOptions.AdditionalProperties"/>) so business code only
/// ever sees <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> (backend-stack ADR). The
/// organization is not part of it: the middleware takes that from the scope it runs in, never
/// from the caller.
/// </summary>
/// <remarks>
/// Every call must carry one: the middleware refuses a call without it before anything reaches
/// the provider, and strips it from the options it passes on, so it never leaves the process.
/// </remarks>
public sealed record ModelInvocationAttribution(ModelInvocationPurpose Purpose, Guid? AccountId, Guid? AssistantId)
{
    /// <summary>The <see cref="AdditionalPropertiesDictionary"/> key the attribution travels under.</summary>
    public const string PropertyName = "smartagri.model_invocation";

    /// <summary>New options carrying only this attribution.</summary>
    public EmbeddingGenerationOptions ToEmbeddingOptions() =>
        new() { AdditionalProperties = new AdditionalPropertiesDictionary { [PropertyName] = this } };

    /// <summary>The attribution <paramref name="options"/> carry, if any.</summary>
    public static ModelInvocationAttribution? From(EmbeddingGenerationOptions? options) =>
        options?.AdditionalProperties is { } properties && properties.TryGetValue(PropertyName, out var value)
            ? value as ModelInvocationAttribution
            : null;

    /// <summary>A copy of <paramref name="options"/> without the attribution (the caller's
    /// instance is left as it was); <see langword="null"/> when nothing else was set.</summary>
    public static EmbeddingGenerationOptions? Strip(EmbeddingGenerationOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var copy = options.Clone();
        if (copy.AdditionalProperties is { } properties)
        {
            var remaining = new AdditionalPropertiesDictionary(properties);
            remaining.Remove(PropertyName);
            copy.AdditionalProperties = remaining.Count == 0 ? null : remaining;
        }

        return copy is { AdditionalProperties: null, ModelId: null, Dimensions: null, RawRepresentationFactory: null } ? null : copy;
    }
}
