namespace SmartAgri.Domain.Organizations;

/// <summary>
/// The conversation retention choices (M6 plan §3 F; withdrawal-and-retention ADR, addendum
/// 2026-10-06): 30, 90, 180 or 365 days, or forever (<see langword="null"/>), the default.
/// </summary>
public static class OrganizationRetention
{
    /// <summary>The offered periods in days, shortest first. Forever is <see langword="null"/>.</summary>
    public static IReadOnlyList<int> Options { get; } = [30, 90, 180, 365];

    /// <summary>How long a shorter retention waits before it applies: nothing is deleted under the new
    /// value until then, and the manager can change it back.</summary>
    public static readonly TimeSpan BufferPeriod = TimeSpan.FromDays(7);

    public static bool IsOffered(int days) => Options.Contains(days);

    /// <summary>Whether <paramref name="candidate"/> keeps conversations for less time than
    /// <paramref name="current"/> (<see langword="null"/> is forever, longer than any number).</summary>
    public static bool IsShorter(int? candidate, int? current) =>
        candidate is { } days && (current is not { } currentDays || days < currentDays);
}

/// <summary>A pending retention taking effect (<see cref="Organization.ApplyDueRetention"/>).</summary>
/// <param name="From">The retention before; <see langword="null"/> for forever.</param>
/// <param name="To">The retention now applying.</param>
public sealed record RetentionSwitch(int? From, int To);
