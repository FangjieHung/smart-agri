using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Primitives;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Application.Notifications;
using SmartAgri.Application.Observability;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// A shop's reservation page (store-reservations design decision R1, §3.1 points 1–5, Slice R1, #389):
/// <c>GET /reserve/{slug}</c> shows the form, <c>POST /reserve/{slug}</c> checks and stores it — anonymous, outside the
/// OpenAPI document, each answering a server-rendered page in Traditional Chinese (<see cref="CoordinatorReservationPages"/>),
/// never JSON.
/// </summary>
/// <remarks>
/// Every answer is <c>no-store</c>, <c>Referrer-Policy: no-referrer</c>, <c>noindex</c>, with a CSP that loads nothing
/// (the page's own style and script by hash). An unknown slug and a shop whose reservations are off are the same
/// <c>404</c> page. Both methods share one rate limit per client IP (<see cref="PublicRateLimitedEndpoint.CoordinatorReservation"/>,
/// <c>PublicChannels:RateLimits:CoordinatorReservationRequestsPerIpPerMinute</c>): beyond it a <c>429</c> page with
/// <c>Retry-After</c>. The path's first segment <c>reserve</c> is the Api's, never the admin SPA's.
/// </remarks>
public static class CoordinatorReservationEndpoints
{
    public const string Route = "/reserve/{slug}";

    /// <summary>The page of <paramref name="slug"/> under <paramref name="publicBaseUrl"/>.</summary>
    public static string Url(string publicBaseUrl, string slug) => $"{publicBaseUrl.TrimEnd('/')}/reserve/{slug}";

    public static IEndpointRouteBuilder MapCoordinatorReservationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, ShowAsync)
            .AllowAnonymous().ExcludeFromDescription().WithPublicRateLimit(PublicRateLimitedEndpoint.CoordinatorReservation);
        endpoints.MapPost(Route, SubmitAsync)
            .AllowAnonymous().ExcludeFromDescription().WithPublicRateLimit(PublicRateLimitedEndpoint.CoordinatorReservation);
        return endpoints;
    }

    private static Task<IResult> ShowAsync(HttpContext context, string slug, CoordinatorReservationFlow flow) =>
        flow.ShowAsync(Anonymous(context), slug);

    private static Task<IResult> SubmitAsync(HttpContext context, string slug, CoordinatorReservationFlow flow) =>
        flow.SubmitAsync(Anonymous(context), slug);

    /// <summary>Anonymous by design: a sign-in cookie sent along must not decide anything.</summary>
    private static HttpContext Anonymous(HttpContext context)
    {
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        CoordinatorReservationPages.SetHeaders(context.Response);
        return context;
    }
}

/// <summary>A shop that takes reservations: its organization and settings (with <see cref="ShopReservationSettings.IsOpen"/>).</summary>
public sealed record ReservationShop(Guid OrganizationId, ShopSettingsDocument Settings)
{
    public ShopReservationSettings Reservations => Settings.Reservations!;
}

/// <summary>What a customer filled in, as typed (trimmed), for showing the form again.</summary>
public sealed record ReservationFormValues(
    string? Date = null,
    string? Time = null,
    string? PartySize = null,
    string? Name = null,
    string? Phone = null,
    string? Email = null,
    string? Note = null);

/// <summary>The form's fields (and their element ids).</summary>
public static class ReservationFields
{
    public const string Date = "date";
    public const string Time = "time";
    public const string PartySize = "party";
    public const string Name = "name";
    public const string Phone = "phone";
    public const string Email = "email";
    public const string Note = "note";

    /// <summary>The hidden field people never see and bots fill in.</summary>
    public const string Honeypot = "website";

    /// <summary>The form's one-time key (<see cref="CoordinatorReservation.NewSubmissionKey"/>).</summary>
    public const string SubmissionKey = "key";

    /// <summary>Which button was pressed: <see cref="ShowTimesStep"/> (no JavaScript: show the chosen date's times) or
    /// the submit button.</summary>
    public const string Step = "step";

    public const string ShowTimesStep = "times";
}

/// <summary>
/// The steps of <see cref="CoordinatorReservationEndpoints"/> (#389). The server computes the times itself
/// (<see cref="ReservationSlots"/>) on every request and never trusts a posted one; the customer's name, phone, Email and
/// note never reach a log — logs carry the organization, the reservation id, the date, the party size and why a form was
/// refused.
/// </summary>
public sealed class CoordinatorReservationFlow
{
    /// <summary>Largest form body read (bytes).</summary>
    public const int MaxFormBytes = 16 * 1024;

    private const int MaxFormFields = 16;

    private readonly CoordinatorLookup _lookup;
    private readonly CoordinatorShops _shops;
    private readonly CoordinatorCommands _commands;
    private readonly CoordinatorReservations _reservations;
    private readonly IEmailSender _email;
    private readonly TimeProvider _clock;
    private readonly ILogger<CoordinatorReservationFlow> _logger;

    public CoordinatorReservationFlow(
        CoordinatorLookup lookup,
        CoordinatorShops shops,
        CoordinatorCommands commands,
        CoordinatorReservations reservations,
        IEmailSender email,
        TimeProvider clock,
        ILogger<CoordinatorReservationFlow> logger)
    {
        _lookup = lookup;
        _shops = shops;
        _commands = commands;
        _reservations = reservations;
        _email = email;
        _clock = clock;
        _logger = logger;
    }

    /// <summary><c>GET /reserve/{slug}</c>, optionally <c>?date=yyyy-MM-dd</c> to start on that date.</summary>
    public async Task<IResult> ShowAsync(HttpContext context, string slug)
    {
        try
        {
            if (await FindShopAsync(slug, context.RequestAborted) is not { } shop)
            {
                return CoordinatorReservationPages.NotFound();
            }

            var page = await PageAsync(shop, context.RequestAborted);
            var date = Single(context.Request.Query[ReservationFields.Date]);
            return CoordinatorReservationPages.Form(
                page with { Values = new ReservationFormValues(Date: date), SubmissionKey = CoordinatorReservation.NewSubmissionKey() },
                StatusCodes.Status200OK);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogError("The reservation page {Slug} could not be shown: {Failure}", Sanitized(slug), ExceptionSummary.Of(exception));
            return CoordinatorReservationPages.Unavailable();
        }
    }

    /// <summary><c>POST /reserve/{slug}</c>: the form, or its 「查看這天的時段」 button.</summary>
    public async Task<IResult> SubmitAsync(HttpContext context, string slug)
    {
        var cancellationToken = context.RequestAborted;
        try
        {
            if (await FindShopAsync(slug, cancellationToken) is not { } shop)
            {
                return CoordinatorReservationPages.NotFound();
            }

            var page = await PageAsync(shop, cancellationToken);
            var form = await ReadFormAsync(context);
            if (form is null)
            {
                _logger.LogInformation("A reservation for organization {OrganizationId} was refused: not a small URL-encoded form.", shop.OrganizationId);
                return CoordinatorReservationPages.Form(
                    page with { SubmissionKey = CoordinatorReservation.NewSubmissionKey(), General = CoordinatorReservationPages.UnreadableFormMessage },
                    StatusCodes.Status400BadRequest);
            }

            var values = Values(form, page.EmailOffered);
            var key = Single(form[ReservationFields.SubmissionKey]);
            if (!CoordinatorReservation.IsSubmissionKey(key))
            {
                _logger.LogInformation("A reservation for organization {OrganizationId} was refused: no valid form key.", shop.OrganizationId);
                return CoordinatorReservationPages.Form(
                    page with { Values = values, SubmissionKey = CoordinatorReservation.NewSubmissionKey(), General = CoordinatorReservationPages.ResubmitMessage },
                    StatusCodes.Status400BadRequest);
            }

            page = page with { Values = values, SubmissionKey = key! };
            if (Single(form[ReservationFields.Step]) == ReservationFields.ShowTimesStep)
            {
                return CoordinatorReservationPages.Form(page, StatusCodes.Status200OK);
            }

            // The same form again (a double press, a reload of the result): the reservation it made, unchecked.
            if (await _reservations.FindBySubmissionKeyAsync(shop.OrganizationId, key!, cancellationToken) is { } sentBefore)
            {
                _logger.LogInformation("Reservation {ReservationId} of organization {OrganizationId}: its form was sent again.", sentBefore.Id, shop.OrganizationId);
                return CoordinatorReservationPages.Done(page, Summary.Of(sentBefore, shop.Settings.TimeZone));
            }

            var checkedForm = Check(page, values);
            if (checkedForm.Errors.Count > 0)
            {
                _logger.LogInformation(
                    "A reservation for organization {OrganizationId} was refused: {Fields}.",
                    shop.OrganizationId, string.Join(", ", checkedForm.Errors.Keys.Order(StringComparer.Ordinal)));
                return CoordinatorReservationPages.Form(page with { Errors = checkedForm.Errors }, StatusCodes.Status400BadRequest);
            }

            var now = _clock.GetUtcNow();
            var reservation = CoordinatorReservation.Submit(
                shop.OrganizationId, checkedForm.Slot!, checkedForm.PartySize, checkedForm.Name!, checkedForm.Phone!, checkedForm.Email, checkedForm.Note, key!, now);
            if (!string.IsNullOrEmpty(Single(form[ReservationFields.Honeypot])))
            {
                // A bot: it sees the same page as a person and nothing is stored.
                _logger.LogInformation("A reservation for organization {OrganizationId} was dropped: the hidden field was filled in.", shop.OrganizationId);
                return CoordinatorReservationPages.Done(page, Summary.Of(reservation, shop.Settings.TimeZone));
            }

            var result = await _reservations.SubmitAsync(reservation, shop.Reservations.PendingExpiryMinutes, now, cancellationToken);
            switch (result.Outcome)
            {
                case ReservationSubmitOutcome.Stored:
                    _logger.LogInformation(
                        "Reservation {ReservationId} of organization {OrganizationId} received for {Date:yyyy-MM-dd} ({PartySize} people).",
                        reservation.Id, shop.OrganizationId, reservation.Date, reservation.PartySize);
                    return CoordinatorReservationPages.Done(page, Summary.Of(reservation, shop.Settings.TimeZone));

                case ReservationSubmitOutcome.Duplicate:
                    return CoordinatorReservationPages.Done(page, Summary.Of(result.Reservation!, shop.Settings.TimeZone));

                case ReservationSubmitOutcome.TooManyPending:
                    _logger.LogInformation(
                        "A reservation for organization {OrganizationId} was refused: the phone number has {Count} pending reservations.",
                        shop.OrganizationId, CoordinatorReservation.MaxPendingPerPhone);
                    return CoordinatorReservationPages.Form(
                        page with
                        {
                            Errors = new Dictionary<string, string>
                            {
                                [ReservationFields.Phone] = CoordinatorReservationPages.TooManyPendingMessage(shop.Reservations.Phone!),
                            },
                        },
                        StatusCodes.Status400BadRequest);

                default:
                    _logger.LogWarning("A reservation for organization {OrganizationId} was refused: its form key is another shop's.", shop.OrganizationId);
                    return CoordinatorReservationPages.Form(
                        page with { SubmissionKey = CoordinatorReservation.NewSubmissionKey(), General = CoordinatorReservationPages.ResubmitMessage },
                        StatusCodes.Status400BadRequest);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("A reservation on page {Slug} could not be handled: {Failure}", Sanitized(slug), ExceptionSummary.Of(exception));
            return CoordinatorReservationPages.Unavailable();
        }
    }

    /// <summary>The shop whose reservation page <paramref name="slug"/> is, when it takes reservations.</summary>
    private async Task<ReservationShop?> FindShopAsync(string? slug, CancellationToken cancellationToken)
    {
        if (ShopReservationSettings.NormalizeSlug(slug) is not { } normalized
            || await _lookup.FindReservationShopAsync(normalized, cancellationToken) is not { } organizationId
            || await _shops.FindAsync(organizationId, cancellationToken) is not { Reservations.IsOpen: true } settings)
        {
            return null;
        }

        return new ReservationShop(organizationId, settings);
    }

    /// <summary>What every page of <paramref name="shop"/> needs: the bookable dates and times now.</summary>
    private async Task<ReservationPage> PageAsync(ReservationShop shop, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var hours = shop.Settings.Hours;
        var today = hours.DateAt(now);
        var commands = (await _commands.ReopenCandidatesAsync(shop.OrganizationId, today.AddDays(-1), cancellationToken))
            .Select(ShopCommandFact.Of)
            .ToList();
        var slots = ReservationSlots.Dates(shop.Settings, now, commands)
            .Select(date => (Date: date, Slots: ReservationSlots.On(shop.Settings, date, now, commands)))
            .ToList();
        return new ReservationPage(shop, today, slots, _email.IsConfigured);
    }

    private static ReservationFormValues Values(IFormCollection form, bool emailOffered)
    {
        string? Text(string name) => Single(form[name]) is { } value && value.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        var note = Single(form[ReservationFields.Note])?.ReplaceLineEndings("\n").Trim();
        return new ReservationFormValues(
            Text(ReservationFields.Date),
            Text(ReservationFields.Time),
            Text(ReservationFields.PartySize),
            Text(ReservationFields.Name),
            Text(ReservationFields.Phone),
            emailOffered ? Text(ReservationFields.Email) : null,
            string.IsNullOrEmpty(note) ? null : note);
    }

    /// <summary>Every field's check, with the message shown next to it; the slot from the server's own times.</summary>
    private static CheckedForm Check(ReservationPage page, ReservationFormValues values)
    {
        var errors = new Dictionary<string, string>();
        var reservations = page.Shop.Reservations;
        ReservationSlot? slot = null;
        if (!DateOnly.TryParseExact(values.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            errors[ReservationFields.Date] = "請選擇日期。";
        }
        else if (page.SlotsOn(date) is not { Count: > 0 } slots)
        {
            errors[ReservationFields.Date] = "這天已經不能線上訂位，請選擇其他日期。";
        }
        else if (values.Time is null)
        {
            errors[ReservationFields.Time] = "請選擇時間。";
        }
        else if ((slot = slots.FirstOrDefault(candidate => candidate.Value == values.Time)) is null)
        {
            errors[ReservationFields.Time] = $"這個時間不能訂了，請重新選擇 {ShopAnnouncements.FormatDate(date)} 的時間。";
        }

        var partySize = 0;
        if (values.PartySize is null
            || !int.TryParse(values.PartySize, NumberStyles.None, CultureInfo.InvariantCulture, out partySize))
        {
            errors[ReservationFields.PartySize] = "請選擇人數。";
        }
        else if (partySize < 1 || partySize > reservations.MaxPartySize)
        {
            errors[ReservationFields.PartySize] = $"人數要在 1 到 {reservations.MaxPartySize} 位之間；更多人請來電 {reservations.Phone}。";
        }

        if (ReservationContact.NameProblem(values.Name) is { } nameProblem)
        {
            errors[ReservationFields.Name] = nameProblem;
        }

        if (ReservationContact.PhoneProblem(values.Phone) is { } phoneProblem)
        {
            errors[ReservationFields.Phone] = phoneProblem;
        }

        if (ReservationContact.EmailProblem(values.Email) is { } emailProblem)
        {
            errors[ReservationFields.Email] = emailProblem;
        }

        if (ReservationContact.NoteProblem(values.Note) is { } noteProblem)
        {
            errors[ReservationFields.Note] = noteProblem;
        }

        return new CheckedForm(errors, slot, partySize, values.Name, ReservationContact.NormalizePhone(values.Phone), values.Email, values.Note);
    }

    /// <summary>The fields: <c>application/x-www-form-urlencoded</c> with a <c>Content-Length</c> of at most
    /// <see cref="MaxFormBytes"/> and a few fields; otherwise <see langword="null"/>. Read through the request's own form
    /// feature: the authentication middleware may already have read (and kept) it.</summary>
    private static async Task<IFormCollection?> ReadFormAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength is not (> 0 and <= MaxFormBytes)
            || !string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var form = await request.ReadFormAsync(context.RequestAborted);
            return form.Count <= MaxFormFields ? form : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string? Single(StringValues values) => values.Count == 1 ? values[0] : null;

    /// <summary>A path's slug as it may be logged: a valid one lowercased, anything else <c>invalid</c>.</summary>
    private static string Sanitized(string? slug) => ShopReservationSettings.NormalizeSlug(slug) ?? "invalid";

    private sealed record CheckedForm(
        IReadOnlyDictionary<string, string> Errors, ReservationSlot? Slot, int PartySize, string? Name, string? Phone, string? Email, string? Note);
}

/// <summary>Everything a form page shows: the shop, today, each bookable date's times, whether Email is asked for, what
/// was filled in, the form's key and the messages.</summary>
public sealed record ReservationPage(
    ReservationShop Shop,
    DateOnly Today,
    IReadOnlyList<(DateOnly Date, IReadOnlyList<ReservationSlot> Slots)> Dates,
    bool EmailOffered)
{
    public ReservationFormValues Values { get; init; } = new();

    public string SubmissionKey { get; init; } = string.Empty;

    /// <summary>Messages by field (<see cref="ReservationFields"/>).</summary>
    public IReadOnlyDictionary<string, string> Errors { get; init; } = new Dictionary<string, string>();

    /// <summary>A message about the whole form.</summary>
    public string? General { get; init; }

    /// <summary>The times of <paramref name="date"/>; empty when it is not bookable.</summary>
    public IReadOnlyList<ReservationSlot> SlotsOn(DateOnly date) =>
        Dates.FirstOrDefault(entry => entry.Date == date).Slots ?? [];

    /// <summary>The date the form shows: the one filled in when it is bookable, else the first bookable one.</summary>
    public DateOnly? SelectedDate =>
        DateOnly.TryParseExact(Values.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && Dates.Any(entry => entry.Date == date)
            ? date
            : Dates.Count > 0 ? Dates[0].Date : null;
}

/// <summary>What the 「已送出」 page lists.</summary>
public sealed record ReservationSummary(
    DateOnly Date, DateTime Local, int PartySize, string Name, string Phone, string? Email, string? Note);

internal static class Summary
{
    public static ReservationSummary Of(CoordinatorReservation reservation, TimeZoneInfo zone) => new(
        reservation.Date,
        TimeZoneInfo.ConvertTime(reservation.ReservedAt, zone).DateTime,
        reservation.PartySize,
        reservation.CustomerName,
        reservation.CustomerPhone,
        reservation.CustomerEmail,
        reservation.Note);
}
