using System.Text.RegularExpressions;

namespace SmartAgri.Domain.Coordinator;

/// <summary>
/// A shop's online reservations (store-reservations design §3.1 points 1–3, Slice R1, #389): the <c>reservations</c>
/// block of its settings file. When <see cref="Enabled"/>, customers book at <c>{PUBLIC_BASE_URL}/reserve/{slug}</c>
/// (<see cref="ReservationSlots"/> says which times); otherwise that page is the same 「找不到」 page as an unknown slug.
/// </summary>
/// <param name="Slug">The page's name in the URL, unique across all shops: <see cref="IsValidSlug"/>. Required when
/// enabled; a disabled block may keep it (it stays reserved for the shop).</param>
/// <param name="Phone">The shop's phone number as it is shown on the page and in the Emails (digits, spaces,
/// <c>-</c>, <c>+</c>, parentheses). Required when enabled.</param>
/// <param name="SlotMinutes">Between two times offered, counted from each business period's opening.</param>
/// <param name="MinLeadMinutes">How long before a time it can still be booked (decision R4: 2 hours).</param>
/// <param name="MaxDaysAhead">The last date offered is today plus this many days (decision R4: 30).</param>
/// <param name="LastSeatingMinutes">The last time offered is this long before the closing (a 21:00 closing and 60:
/// 20:00 is the last).</param>
/// <param name="MaxPartySize">The most people one reservation can be for.</param>
/// <param name="PendingExpiryMinutes">A reservation nobody handled expires this long before its time (§3.1 point 6,
/// Slice R2); less than <see cref="MinLeadMinutes"/>, so even the latest booking leaves the shop time to answer.</param>
public sealed partial record ShopReservationSettings(
    bool Enabled,
    string? Slug,
    string? Phone,
    int SlotMinutes = ShopReservationSettings.DefaultSlotMinutes,
    int MinLeadMinutes = ShopReservationSettings.DefaultMinLeadMinutes,
    int MaxDaysAhead = ShopReservationSettings.DefaultMaxDaysAhead,
    int LastSeatingMinutes = ShopReservationSettings.DefaultLastSeatingMinutes,
    int MaxPartySize = ShopReservationSettings.DefaultMaxPartySize,
    int PendingExpiryMinutes = ShopReservationSettings.DefaultPendingExpiryMinutes)
{
    public const int DefaultSlotMinutes = 30;
    public const int DefaultMinLeadMinutes = 120;
    public const int DefaultMaxDaysAhead = 30;
    public const int DefaultLastSeatingMinutes = 60;
    public const int DefaultMaxPartySize = 20;
    public const int DefaultPendingExpiryMinutes = 60;

    public const int SlugMinLength = 3;
    public const int SlugMaxLength = 40;

    /// <summary>Longest <see cref="Phone"/> (characters).</summary>
    public const int PhoneMaxLength = 20;

    /// <summary>The intervals <see cref="SlotMinutes"/> may be: each divides an hour, so times line up.</summary>
    public static readonly IReadOnlyList<int> SlotMinuteChoices = [10, 15, 20, 30, 60];

    /// <summary>Longest <see cref="MinLeadMinutes"/>: a week.</summary>
    public const int MaxMinLeadMinutes = 7 * 24 * 60;

    /// <summary>Longest <see cref="MaxDaysAhead"/>: as far as a 店休 command can reach (<see cref="CoordinatorCommand.MaxDaysAhead"/>),
    /// so every reservation can still be affected by one (Slice R4).</summary>
    public const int MaxMaxDaysAhead = CoordinatorCommand.MaxDaysAhead;

    /// <summary>Longest <see cref="LastSeatingMinutes"/>: 12 hours.</summary>
    public const int MaxLastSeatingMinutes = 12 * 60;

    /// <summary>Largest <see cref="MaxPartySize"/>.</summary>
    public const int MaxMaxPartySize = 100;

    /// <summary>Longest <see cref="PendingExpiryMinutes"/>: a day.</summary>
    public const int MaxPendingExpiryMinutes = 24 * 60;

    /// <summary>Whether reservations are open: enabled with a slug and a phone (the settings file guarantees both).</summary>
    public bool IsOpen => Enabled && Slug is not null && Phone is not null;

    /// <summary>
    /// A slug: <see cref="SlugMinLength"/>–<see cref="SlugMaxLength"/> lowercase letters, digits and <c>-</c>, not
    /// starting or ending with <c>-</c> (<c>wangji</c>, <c>wang-ji-2</c>).
    /// </summary>
    public static bool IsValidSlug(string? slug) =>
        slug is { Length: >= SlugMinLength and <= SlugMaxLength } && SlugPattern().IsMatch(slug);

    /// <summary>A slug from a URL path: lowercased when it is one, else <see langword="null"/> (so <c>/reserve/WangJi</c>
    /// is <c>wangji</c>'s page).</summary>
    public static string? NormalizeSlug(string? text)
    {
        if (text is null || text.Length is < SlugMinLength or > SlugMaxLength)
        {
            return null;
        }

        var lower = text.ToLowerInvariant();
        return IsValidSlug(lower) ? lower : null;
    }

    /// <summary>A phone number as a shop writes it: at most <see cref="PhoneMaxLength"/> characters of digits, spaces,
    /// <c>-</c>, <c>+</c> and parentheses, 8 to 15 of them digits, <c>+</c> only first.</summary>
    public static bool IsValidPhone(string? phone) =>
        phone is { Length: > 0 and <= PhoneMaxLength }
        && ShopPhonePattern().IsMatch(phone)
        && phone.Count(char.IsAsciiDigit) is >= 8 and <= 15;

    /// <summary>The <c>tel:</c> link of <paramref name="phone"/> (<see cref="IsValidPhone"/>): its digits, and a
    /// leading <c>+</c>.</summary>
    public static string TelUri(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);
        return "tel:" + (phone.StartsWith('+') ? "+" : string.Empty) + new string([.. phone.Where(char.IsAsciiDigit)]);
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^\+?[0-9() -]+$")]
    private static partial Regex ShopPhonePattern();
}
