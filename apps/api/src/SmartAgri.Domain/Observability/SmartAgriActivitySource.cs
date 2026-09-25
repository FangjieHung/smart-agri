using System.Diagnostics;

namespace SmartAgri.Domain.Observability;

/// <summary>
/// The application-wide <see cref="ActivitySource"/> for custom spans — e.g. M2's model
/// call timing and token usage (see M1 skeleton plan, Slice 3, and the observability ADR).
/// Lives in Domain (no third-party dependency: <see cref="ActivitySource"/> is BCL) so
/// every layer, including future M2 code, can start spans from the same source without a
/// reference to the Api project. It is registered with the OpenTelemetry SDK's tracer
/// provider via <c>AddSource(SmartAgriActivitySource.Name)</c> in
/// <c>SmartAgri.Api/Observability/OpenTelemetryExtensions.cs</c>.
/// </summary>
public static class SmartAgriActivitySource
{
    public const string Name = "SmartAgri";

    /// <summary>
    /// The shared <see cref="ActivitySource"/> instance. Start spans with
    /// <c>SmartAgriActivitySource.Instance.StartActivity("...")</c>.
    /// </summary>
    public static readonly ActivitySource Instance = new(Name);

    /// <summary>
    /// Tag key for the current organization on a span. Carries only the organization's id
    /// — never account names or emails (observability ADR). Organizations don't exist yet
    /// (see issue #4); this constant is provided now so that work has a single, agreed tag
    /// name to set once <c>IOrganizationContext</c> lands, instead of inventing one later.
    /// </summary>
    public const string OrganizationIdTag = "smartagri.organization_id";
}
