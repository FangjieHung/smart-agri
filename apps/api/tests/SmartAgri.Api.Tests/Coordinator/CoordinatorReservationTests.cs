using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Notifications;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// A shop's reservation page (store-reservations design Slice R1, #389) through the real endpoints and PostgreSQL: the
/// page offers only the times the server computes (weekly hours, last seating, lead time, days ahead, confirmed 店休
/// commands), every field is checked again on <c>POST</c>, a form sent twice is one reservation, a phone has at most
/// three pending ones, bots filling the hidden field get the same page and nothing is stored, unknown and closed pages
/// are the same <c>404</c>, and the customer's details never reach a log. Also the operator's commands: the reservation
/// address of <c>coordinator-shop</c>, a slug another shop has, and <c>coordinator-reservations list</c>.
/// </summary>
/// <remarks>Every shop is open every day 11:00–14:00 and 17:00–21:00 in Taipei, so the day after tomorrow always has its
/// twelve times whatever the clock says.</remarks>
[Trait("Category", TestCategories.Docker)]
public sealed partial class CoordinatorReservationTests : IClassFixture<CoordinatorReservationHostFixture>
{
    private const string ShopPhone = "02-2345-6789";

    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private readonly CoordinatorReservationHostFixture _host;
    private readonly HttpClient _client;

    public CoordinatorReservationTests(CoordinatorReservationHostFixture host)
    {
        _host = host;
        _client = host.Factory.CreateClient();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_host.Clock.GetUtcNow(), Taipei).DateTime);

    /// <summary>The day after tomorrow: all twelve times bookable.</summary>
    private DateOnly Day => Today.AddDays(2);

    // --- The page -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_shows_the_shop_the_dates_and_the_chosen_days_times_with_strict_headers()
    {
        var (_, slug) = await CreateShopAsync();

        var response = await _client.GetAsync($"/reserve/{slug}?date={Value(Day)}", CancellationToken);
        var html = await response.Content.ReadAsStringAsync(CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe("text/html; charset=utf-8");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        response.Headers.GetValues("X-Robots-Tag").ShouldBe(["noindex"]);
        response.Headers.GetValues("Content-Security-Policy").ShouldBe([CoordinatorReservationPages.FormContentSecurityPolicy]);
        CoordinatorReservationPages.FormContentSecurityPolicy.ShouldStartWith("default-src 'none'; style-src 'sha256-");
        CoordinatorReservationPages.FormContentSecurityPolicy.ShouldContain("script-src 'sha256-");
        CoordinatorReservationPages.FormContentSecurityPolicy.ShouldContain("form-action 'self'");

        html.ShouldContain("<h1>王記麵店</h1>");
        html.ShouldContain($"<a href=\"tel:0223456789\">{ShopPhone}</a>");
        html.ShouldContain($"<option value=\"{Value(Day)}\"");
        Regex.IsMatch(html, $"<option value=\"{Value(Day)}\" data-slots=\"[^\"]*\" selected>").ShouldBeTrue("the date asked for is chosen");
        TimesOf(html).ShouldBe(
            [.. new[] { "11:00", "11:30", "12:00", "12:30", "13:00", "17:00", "17:30", "18:00", "18:30", "19:00", "19:30", "20:00" }
                .Select(time => $"{Value(Day)}T{time}")]);
        html.ShouldContain("name=\"email\"", Case.Sensitive, "the platform can send Email");
        html.ShouldContain("個人資料告知：王記麵店透過本訂位系統蒐集您的姓名、電話、Email與備註");
        html.ShouldContain("資料保存到訂位時間後 90 天，之後刪除。");
        html.ShouldContain("class=\"hp\" aria-hidden=\"true\"");
        CoordinatorReservation.IsSubmissionKey(KeyOf(html)).ShouldBeTrue();
        html.ShouldContain("最晚可訂到打烊前 1 小時；需在 2 小時前訂位。");

        // The slug in any case is the same page; without a date the first bookable one is chosen.
        var upper = await _client.GetStringAsync($"/reserve/{slug.ToUpperInvariant()}", CancellationToken);
        upper.ShouldContain("<h1>王記麵店</h1>");
        var dates = Regex.Matches(upper, "<option value=\"(\\d{4}-\\d{2}-\\d{2})\"").Select(match => match.Groups[1].Value).ToList();
        dates.Count.ShouldBeInRange(30, 31, "today (while it has times left) to today plus 30 days");
        dates[^1].ShouldBe(Value(Today.AddDays(30)));
    }

    [Fact]
    public async Task An_unknown_slug_and_a_shop_without_reservations_are_the_same_404_page()
    {
        var (closed, closedSlug) = await CreateShopAsync(enabled: false);
        var unknown = "unknown-" + Guid.NewGuid().ToString("N")[..8];
        var posted = new[] { Fields(Day, "18:00"), Fields(Day, "18:00") };

        var pages = new List<HttpResponseMessage>
        {
            await _client.GetAsync($"/reserve/{unknown}", CancellationToken),
            await _client.GetAsync($"/reserve/{closedSlug}", CancellationToken),
            await _client.GetAsync("/reserve/x", CancellationToken),
            await _client.GetAsync("/reserve/not_a_slug", CancellationToken),
            await _client.PostAsync($"/reserve/{unknown}", Form(posted[0]), CancellationToken),
            await _client.PostAsync($"/reserve/{closedSlug}", Form(posted[1]), CancellationToken),
        };

        var first = await pages[0].Content.ReadAsStringAsync(CancellationToken);
        first.ShouldContain(CoordinatorReservationPages.NotFoundTitle);
        foreach (var page in pages)
        {
            page.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            page.Headers.GetValues("Content-Security-Policy").ShouldBe([CoordinatorReservationPages.ContentSecurityPolicy]);
            page.Headers.CacheControl!.NoStore.ShouldBeTrue();
            (await page.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(first);
        }

        first.ShouldNotContain("王記");
        (await ReservationsAsync(closed)).ShouldBeEmpty();
        var keys = posted.Select(fields => fields["key"]).ToList();
        (await ReservationsAsync()).ShouldNotContain(reservation => keys.Contains(reservation.SubmissionKey));
    }

    // --- Sending the form -----------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_form_is_stored_once_in_UTC_with_the_phone_in_digits_and_a_resend_answers_the_same_page()
    {
        var (shop, slug) = await CreateShopAsync();
        var key = KeyOf(await FormPageAsync(slug));
        var fields = Fields(Day, "18:30", key, party: "4", name: "陳小姐", phone: "+886 912-345-678", email: "chen@example.com", note: "需要兒童椅\r\n靠窗");

        var sent = await _client.PostAsync($"/reserve/{slug}", Form(fields), CancellationToken);
        var html = await sent.Content.ReadAsStringAsync(CancellationToken);

        sent.StatusCode.ShouldBe(HttpStatusCode.OK, html);
        html.ShouldContain($"<title>{CoordinatorReservationPages.DoneTitle}｜王記麵店</title>");
        html.ShouldContain($"{ShopAnnouncements.FormatDate(Day)} 18:30");
        html.ShouldContain("<dd>4 位</dd>");
        html.ShouldContain("<dd>0912-345-678</dd>");
        html.ShouldContain("<dd>需要兒童椅\n靠窗</dd>");
        html.ShouldContain(CoordinatorReservationPages.DoneWithEmailMessage);
        html.ShouldContain($"如需更改或取消，請來電 <a href=\"tel:0223456789\">{ShopPhone}</a>。");
        sent.Headers.GetValues("Content-Security-Policy").ShouldBe([CoordinatorReservationPages.ContentSecurityPolicy]);

        var stored = (await ReservationsAsync(shop)).ShouldHaveSingleItem();
        stored.Date.ShouldBe(Day);
        stored.ReservedAt.ShouldBe(new DateTimeOffset(Day.ToDateTime(new TimeOnly(18, 30)), TimeSpan.FromHours(8)));
        stored.ReservedAt.Offset.ShouldBe(TimeSpan.Zero);
        (stored.PartySize, stored.CustomerName, stored.CustomerPhone, stored.CustomerEmail, stored.Note)
            .ShouldBe((4, "陳小姐", "0912345678", "chen@example.com", "需要兒童椅\n靠窗"));
        (stored.Status, stored.Version, stored.MembersNotified, stored.SubmissionKey)
            .ShouldBe((CoordinatorReservationStatus.Pending, 1, CoordinatorReservationNotice.Pending, key));

        // The same form again — a double press or a reload of the result — is the same page and no second row.
        var again = await _client.PostAsync($"/reserve/{slug}", Form(fields), CancellationToken);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(html);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _client.PostAsync($"/reserve/{slug}", Form(fields), CancellationToken)));
        concurrent.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        (await ReservationsAsync(shop)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Without_an_email_address_the_shop_calls_and_two_forms_are_two_reservations()
    {
        var (shop, slug) = await CreateShopAsync();

        var first = await SendAsync(slug, Fields(Day, "11:00", phone: "0912 000 111"));
        var second = await SendAsync(slug, Fields(Day.AddDays(1), "20:00", phone: "0912000111"));

        first.ShouldContain(CoordinatorReservationPages.DoneWithoutEmailMessage);
        first.ShouldNotContain(CoordinatorReservationPages.DoneWithEmailMessage);
        second.ShouldContain($"{ShopAnnouncements.FormatDate(Day.AddDays(1))} 20:00");
        (await ReservationsAsync(shop)).Select(reservation => (reservation.Date, reservation.CustomerEmail)).ShouldBe(
            [(Day, (string?)null), (Day.AddDays(1), null)], ignoreOrder: true);
    }

    [Fact]
    public async Task Without_smtp_the_email_field_is_not_offered_and_an_address_sent_anyway_is_dropped()
    {
        var (shop, slug) = await CreateShopAsync();
        _host.Email.IsConfigured = false;
        try
        {
            var page = await FormPageAsync(slug);
            page.ShouldNotContain("name=\"email\"");
            page.ShouldContain("蒐集您的姓名、電話與備註");

            var done = await SendAsync(slug, Fields(Day, "12:00", email: "chen@example.com"));

            done.ShouldContain(CoordinatorReservationPages.DoneWithoutEmailMessage);
            done.ShouldNotContain("chen@example.com");
            (await ReservationsAsync(shop)).ShouldHaveSingleItem().CustomerEmail.ShouldBeNull();
        }
        finally
        {
            _host.Email.IsConfigured = true;
        }
    }

    [Fact]
    public async Task Each_wrong_field_is_shown_next_to_it_with_what_was_typed_and_nothing_is_stored()
    {
        var (shop, slug) = await CreateShopAsync();
        var fields = Fields(Day, time: null, party: "0", name: "<b>陳</b>", phone: "12345", email: "chen@", note: new string('長', 201));

        var refused = await _client.PostAsync($"/reserve/{slug}", Form(fields), CancellationToken);
        var html = await refused.Content.ReadAsStringAsync(CancellationToken);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        html.ShouldContain("請修正以下欄位：");
        html.ShouldContain("<p class=\"error\" id=\"time-error\">請選擇時間。</p>");
        html.ShouldContain("<p class=\"error\" id=\"party-error\">人數要在 1 到 20 位之間；更多人請來電 02-2345-6789。</p>");
        html.ShouldContain("<p class=\"error\" id=\"phone-error\">請填寫台灣的手機或市話號碼，例如 0912-345-678 或 02-2345-6789。</p>");
        html.ShouldContain("<p class=\"error\" id=\"email-error\">Email 的格式不正確，例如 name@example.com。</p>");
        html.ShouldContain("<p class=\"error\" id=\"note-error\">備註最多 200 個字（目前 201 個）。</p>");
        html.ShouldNotContain("id=\"name-error\"");
        html.ShouldContain("value=\"&lt;b&gt;陳&lt;/b&gt;\"", Case.Sensitive, "what was typed, encoded");
        html.ShouldNotContain("<b>陳</b>");
        html.ShouldContain("value=\"12345\"");
        html.ShouldContain("aria-describedby=\"phone-hint phone-error\" aria-invalid=\"true\"");
        html.ShouldContain("<a href=\"#phone\">");
        KeyOf(html).ShouldBe(fields["key"], "the same form, so a resend after fixing it is still one reservation");

        var blank = await _client.PostAsync($"/reserve/{slug}", Form(Fields(Day, "18:00", name: " ", phone: "")), CancellationToken);
        var blankHtml = await blank.Content.ReadAsStringAsync(CancellationToken);
        blankHtml.ShouldContain("<p class=\"error\" id=\"name-error\">請填寫姓名。</p>");
        blankHtml.ShouldContain("<p class=\"error\" id=\"phone-error\">請填寫電話。</p>");
        Regex.IsMatch(blankHtml, $"<option value=\"{Value(Day)}T18:00\" selected>").ShouldBeTrue("the chosen time stays chosen");

        (await ReservationsAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Times_the_server_does_not_offer_are_refused_whatever_the_form_says()
    {
        var (shop, slug) = await CreateShopAsync();
        var refusals = new (DateOnly Date, string Time, string Field)[]
        {
            (Day, "20:30", "time"),                     // within the last hour before 21:00
            (Day, "15:00", "time"),                     // between lunch and dinner
            (Day, "18:15", "time"),                     // not on the half hours
            (Day, "21:00", "time"),                     // the closing itself
            (Day.AddDays(1), $"{Value(Day)}T18:00", "time"), // another day's time
            (Today.AddDays(31), "18:00", "date"),       // too far ahead
            (Today.AddDays(-2), "18:00", "date"),       // in the past
        };

        foreach (var (date, time, field) in refusals)
        {
            var fields = Fields(date, time);
            var response = await _client.PostAsync($"/reserve/{slug}", Form(fields), CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{date} {time}");
            (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain($"id=\"{field}-error\"", Case.Sensitive, $"{date} {time}");
        }

        // Within the lead time: today's times up to two hours from now (today's slots are those still offered).
        var now = TimeZoneInfo.ConvertTime(_host.Clock.GetUtcNow(), Taipei);
        var soon = new TimeOnly(now.Hour, now.Minute >= 30 ? 30 : 0).AddMinutes(30);
        var tooSoon = await _client.PostAsync(
            $"/reserve/{slug}", Form(Fields(Today, $"{Value(Today)}T{soon:HH\\:mm}")), CancellationToken);
        tooSoon.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await ReservationsAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Confirmed_closures_and_changed_hours_take_their_times_away_and_a_reopen_gives_them_back()
    {
        var (shop, slug) = await CreateShopAsync();
        var closed = Today.AddDays(3);
        var early = Today.AddDays(4);
        var late = Today.AddDays(5);
        var closure = await ConfirmedCommandAsync(shop, ShopScenario.TemporaryClosure, closed, null);
        await ConfirmedCommandAsync(shop, ShopScenario.CloseEarly, early, new TimeOnly(19, 0));
        await ConfirmedCommandAsync(shop, ShopScenario.OpenLate, late, new TimeOnly(12, 0));
        await CommandAsync(shop, ShopScenario.TemporaryClosure, Day, null, confirm: false);

        var page = await FormPageAsync(slug);
        page.ShouldNotContain($"<option value=\"{Value(closed)}\"");
        page.ShouldContain($"<option value=\"{Value(Day)}\"", Case.Sensitive, "a closure only previewed changes nothing");
        TimesOf(await FormPageAsync(slug, early))[^1].ShouldBe($"{Value(early)}T18:00");
        TimesOf(await FormPageAsync(slug, late))[0].ShouldBe($"{Value(late)}T12:00");

        var refused = await _client.PostAsync($"/reserve/{slug}", Form(Fields(closed, "18:00")), CancellationToken);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("這天已經不能線上訂位，請選擇其他日期。");
        (await _client.PostAsync($"/reserve/{slug}", Form(Fields(early, "19:00")), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.PostAsync($"/reserve/{slug}", Form(Fields(late, "11:30")), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // 恢復營業 confirmed: the day is bookable again.
        await CommandAsync(shop, ShopScenario.Reopen, closed, null, confirm: true, reopens: closure);
        (await FormPageAsync(slug)).ShouldContain($"<option value=\"{Value(closed)}\"");
        (await SendAsync(slug, Fields(closed, "18:00"))).ShouldContain(CoordinatorReservationPages.DoneTitle);
        (await ReservationsAsync(shop)).ShouldHaveSingleItem().Date.ShouldBe(closed);
    }

    [Fact]
    public async Task A_fourth_pending_reservation_of_the_same_phone_is_refused_and_another_phone_is_not()
    {
        var (shop, slug) = await CreateShopAsync();
        foreach (var (spelling, time) in new[] { ("0912-345-678", "11:00"), ("+886 912 345 678", "11:30"), ("0912345678", "12:00") })
        {
            (await SendAsync(slug, Fields(Day, time, phone: spelling))).ShouldContain(CoordinatorReservationPages.DoneTitle);
        }

        var fourth = await _client.PostAsync($"/reserve/{slug}", Form(Fields(Day, "12:30", phone: "0912 345 678")), CancellationToken);

        fourth.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fourth.Content.ReadAsStringAsync(CancellationToken)).ShouldContain(
            $"<p class=\"error\" id=\"phone-error\">{CoordinatorReservationPages.TooManyPendingMessage(ShopPhone)}</p>");
        (await SendAsync(slug, Fields(Day, "12:30", phone: "0987654321"))).ShouldContain(CoordinatorReservationPages.DoneTitle);
        (await ReservationsAsync(shop)).Count.ShouldBe(4);

        // Another shop counts its own.
        var (otherShop, otherSlug) = await CreateShopAsync();
        (await SendAsync(otherSlug, Fields(Day, "12:30", phone: "0912345678"))).ShouldContain(CoordinatorReservationPages.DoneTitle);
        (await ReservationsAsync(otherShop)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_filled_in_hidden_field_gets_the_same_page_and_stores_nothing()
    {
        var (shop, slug) = await CreateShopAsync();
        var fields = Fields(Day, "18:00");
        fields["website"] = "https://spam.example";

        var response = await _client.PostAsync($"/reserve/{slug}", Form(fields), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain(CoordinatorReservationPages.DoneTitle);
        (await ReservationsAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_javascript_the_times_button_shows_the_chosen_days_times_and_keeps_what_was_typed()
    {
        var (shop, slug) = await CreateShopAsync();
        var other = Day.AddDays(1);
        var fields = Fields(other, $"{Value(Day)}T18:00", name: "陳小姐", phone: "0912345678", note: "生日");
        fields["step"] = "times";

        var response = await _client.PostAsync($"/reserve/{slug}", Form(fields), CancellationToken);
        var html = await response.Content.ReadAsStringAsync(CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Regex.IsMatch(html, $"<option value=\"{Value(other)}\" data-slots=\"[^\"]*\" selected>").ShouldBeTrue();
        TimesOf(html).ShouldAllBe(time => time.StartsWith(Value(other), StringComparison.Ordinal));
        html.ShouldNotContain(" selected>18:00", Case.Sensitive, "the other day's time is not chosen");
        html.ShouldContain("value=\"陳小姐\"");
        html.ShouldContain(">生日</textarea>");
        html.ShouldNotContain("class=\"error\"");
        KeyOf(html).ShouldBe(fields["key"]);
        (await ReservationsAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_form_that_cannot_be_read_or_has_no_key_is_shown_again_with_a_new_key()
    {
        var (shop, slug) = await CreateShopAsync();

        using var json = new StringContent("{\"name\":\"陳小姐\"}", Encoding.UTF8, "application/json");
        var unreadable = await _client.PostAsync($"/reserve/{slug}", json, CancellationToken);
        unreadable.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unreadable.Content.ReadAsStringAsync(CancellationToken)).ShouldContain(CoordinatorReservationPages.UnreadableFormMessage);

        var fields = Fields(Day, "18:00", name: "陳小姐");
        fields.Remove("key");
        var keyless = await _client.PostAsync($"/reserve/{slug}", Form(fields), CancellationToken);
        var html = await keyless.Content.ReadAsStringAsync(CancellationToken);
        keyless.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        html.ShouldContain(CoordinatorReservationPages.ResubmitMessage);
        html.ShouldContain("value=\"陳小姐\"");
        CoordinatorReservation.IsSubmissionKey(KeyOf(html)).ShouldBeTrue();

        // A key that is another shop's: nothing stored here, a new key to send again.
        var (_, otherSlug) = await CreateShopAsync();
        var shared = Fields(Day, "18:00");
        await SendAsync(otherSlug, shared);
        var taken = await _client.PostAsync($"/reserve/{slug}", Form(shared), CancellationToken);
        taken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var takenHtml = await taken.Content.ReadAsStringAsync(CancellationToken);
        takenHtml.ShouldContain(CoordinatorReservationPages.ResubmitMessage);
        KeyOf(takenHtml).ShouldNotBe(shared["key"]);
        (await ReservationsAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_customers_details_never_reach_a_log()
    {
        var (shop, slug) = await CreateShopAsync();
        var name = "林" + Guid.NewGuid().ToString("N")[..6];
        var email = $"log-{Guid.NewGuid():N}@example.com";
        var note = "備註" + Guid.NewGuid().ToString("N")[..8];

        _host.Log.Start();
        try
        {
            await _client.GetStringAsync($"/reserve/{slug}?date={Value(Day)}", CancellationToken);
            var times = Fields(Day.AddDays(1), "18:00", name: name, phone: "0933-221-100", email: email, note: note);
            times["step"] = "times";
            await _client.PostAsync($"/reserve/{slug}", Form(times), CancellationToken);
            await _client.PostAsync($"/reserve/{slug}", Form(Fields(Day, "18:00", name: name, phone: "0933-221-100", email: "bad@" + email, note: note)), CancellationToken);
            var fields = Fields(Day, "18:00", name: name, phone: "0933-221-100", email: email, note: note);
            (await SendAsync(slug, fields)).ShouldContain(CoordinatorReservationPages.DoneTitle);
            await _client.PostAsync($"/reserve/{slug}", Form(fields), CancellationToken);
        }
        finally
        {
            _host.Log.Stop();
        }

        var stored = (await ReservationsAsync(shop)).ShouldHaveSingleItem();
        var lines = _host.Log.Lines;
        lines.ShouldContain(line => line.Contains(stored.Id.ToString(), StringComparison.Ordinal), "not vacuous: the reservation was logged");
        foreach (var secret in new[] { name, "0933221100", "0933-221-100", "221-100", email, note })
        {
            lines.ShouldNotContain(line => line.Contains(secret, StringComparison.Ordinal), secret);
        }
    }

    // --- The operator's commands ------------------------------------------------------------------------

    [Fact]
    public async Task Import_and_show_print_the_reservation_address_and_show_keeps_it_out_of_the_file()
    {
        var shop = await _host.CreateOrganizationAsync("訂位網址店");
        var slug = NewSlug();

        var imported = await RunShopWithFileAsync(ShopFile(slug), "import", "--organization", shop.Code);

        imported.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, imported.Error);
        imported.Output.ShouldBe(
            $"已匯入組織 {shop.Code}（王記麵店）的店家設定。\n線上訂位網址：http://localhost:5153/reserve/{slug}\n", StringCompareShould.IgnoreLineEndings);
        var shown = await RunShopAsync("show", "--organization", shop.Code);
        shown.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, shown.Error);
        ShopSettingsJson.Parse(shown.Output).Document!.Reservations!.Slug.ShouldBe(slug);
        shown.Output.ShouldNotContain("線上訂位網址");
        shown.Error.ShouldBe($"線上訂位網址：http://localhost:5153/reserve/{slug}\n", StringCompareShould.IgnoreLineEndings);

        var created = (await ActivitiesAsync(shop)).ShouldHaveSingleItem();
        created.Detail!.ShouldContain("\"reservations\"");
        created.Detail!.ShouldNotContain(slug);

        // Turned off: no address.
        var off = await RunShopWithFileAsync(ShopFile(slug, enabled: false), "import", "--organization", shop.Code);
        off.Output.ShouldBe($"已更新組織 {shop.Code}（王記麵店）的店家設定，變更：reservations。\n", StringCompareShould.IgnoreLineEndings);
        (await RunShopAsync("show", "--organization", shop.Code)).Error.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_slug_another_shop_has_is_refused_and_nothing_is_written()
    {
        var (_, slug) = await CreateShopAsync();
        var second = await _host.CreateOrganizationAsync("搶代號店");

        var refused = await RunShopWithFileAsync(ShopFile(slug, enabled: false), "import", "--organization", second.Code);

        refused.Exit.ShouldBe(CoordinatorShopCommand.ExitUsage);
        refused.Output.ShouldBeEmpty();
        refused.Error.ShouldContain($"$.reservations.slug：「{slug}」已經是另一家店的訂位網址代號，請換一個。");
        await using var dbContext = _host.Postgres.CreateDbContext(second.Id);
        (await dbContext.CoordinatorShopSettings.AnyAsync(CancellationToken)).ShouldBeFalse();
        (await ActivitiesAsync(second)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_reservation_list_masks_the_phone_and_never_prints_the_email_address_or_the_note()
    {
        var (shop, slug) = await CreateShopAsync();
        await SendAsync(slug, Fields(Day.AddDays(1), "20:00", name: "王先生", phone: "0912-111-222"));
        await SendAsync(slug, Fields(Day, "18:30", party: "4", name: "陳小姐", phone: "0912-345-678", email: "chen-list@example.com", note: "需要兒童椅"));

        var listed = await RunReservationsAsync("list", "--organization", shop.Code.ToUpperInvariant());

        listed.Exit.ShouldBe(CoordinatorReservationsCommand.ExitSuccess, listed.Error);
        var lines = listed.Output.ReplaceLineEndings("\n").Split('\n');
        lines[0].ShouldBe($"組織 {shop.Code}（王記麵店）：線上訂位開放中（/reserve/{slug}）。時間為 Asia/Taipei。");
        lines.ShouldContain("即將到來（2 筆）：");
        var chen = lines.Single(line => line.Contains("陳小姐", StringComparison.Ordinal));
        chen.ShouldStartWith($"  {Day:yyyy-MM-dd} 18:30（{ShopAnnouncements.FormatDate(Day)}）｜4 位｜陳小姐｜…678｜pending｜尚未通知成員｜有 Email｜有備註｜送出 ");
        lines.ToList().IndexOf(chen).ShouldBeLessThan(lines.ToList().FindIndex(line => line.Contains("王先生", StringComparison.Ordinal)), "earliest first");
        lines.Single(line => line.Contains("王先生", StringComparison.Ordinal)).ShouldContain("｜…222｜pending｜尚未通知成員｜無 Email｜送出 ");
        listed.Output.ShouldContain("最近 7 天（0 筆）：");
        foreach (var hidden in new[] { "0912345678", "0912-345-678", "0912111222", "chen-list@example.com", "需要兒童椅" })
        {
            listed.Output.ShouldNotContain(hidden);
        }

        (await RunReservationsAsync("list", "--organization", "nobody-" + Guid.NewGuid().ToString("N")[..6])).Exit
            .ShouldBe(CoordinatorReservationsCommand.ExitFailed);
        (await RunReservationsAsync("list")).Exit.ShouldBe(CoordinatorReservationsCommand.ExitUsage);
        (await RunReservationsAsync("list", "--organization", shop.Code, "--days", "91")).Exit.ShouldBe(CoordinatorReservationsCommand.ExitUsage);
        (await RunReservationsAsync("show", "--organization", shop.Code)).Exit.ShouldBe(CoordinatorReservationsCommand.ExitUsage);
    }

    // --- Helpers --------------------------------------------------------------------------------------------

    /// <summary>A settings file: open every day 11:00–14:00 and 17:00–21:00, reservations at <paramref name="slug"/>.</summary>
    private static string ShopFile(string slug, bool enabled = true)
    {
        var day = new JsonArray(
            new JsonObject { ["open"] = "11:00", ["close"] = "14:00" },
            new JsonObject { ["open"] = "17:00", ["close"] = "21:00" });
        var week = new JsonObject();
        foreach (var name in new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" })
        {
            week[name] = day.DeepClone();
        }

        var file = JsonNode.Parse(
            """
            {
              "storeName": "王記麵店",
              "timeZone": "Asia/Taipei",
              "scenarios": {
                "temporary-closure": { "enabled": true, "announcement": { "template": "{店名} {日期} 臨時店休。" }, "actions": [{ "kind": "line-broadcast", "mode": "reminder", "steps": "群發。" }] },
                "close-early": { "enabled": true, "announcement": { "template": "{店名} {日期} 提早到 {時間} 打烊。" }, "actions": [{ "kind": "line-broadcast", "mode": "reminder", "steps": "群發。" }] },
                "open-late": { "enabled": true, "announcement": { "template": "{店名} {日期} 延後到 {時間} 開店。" }, "actions": [{ "kind": "line-broadcast", "mode": "reminder", "steps": "群發。" }] },
                "reopen": { "enabled": true }
              }
            }
            """)!.AsObject();
        file["weeklyHours"] = week;
        file["reservations"] = new JsonObject { ["enabled"] = enabled, ["slug"] = slug, ["phone"] = ShopPhone };
        return file.ToJsonString();
    }

    private static string NewSlug() => "shop-" + Guid.NewGuid().ToString("N")[..12];

    private async Task<(Organization Shop, string Slug)> CreateShopAsync(bool enabled = true)
    {
        var shop = await _host.CreateOrganizationAsync("訂位測試店");
        var slug = NewSlug();
        var parsed = ShopSettingsJson.Parse(ShopFile(slug, enabled));
        parsed.Errors.ShouldBeEmpty(string.Join('\n', parsed.Errors));
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<CoordinatorShops>().ImportAsync(shop.Id, parsed.Document!, CancellationToken))
            .Outcome.ShouldBe(ShopImportOutcome.Created);
        return (shop, slug);
    }

    private async Task<string> FormPageAsync(string slug, DateOnly? date = null)
    {
        var response = await _client.GetAsync($"/reserve/{slug}{(date is { } day ? $"?date={Value(day)}" : string.Empty)}", CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(CancellationToken);
    }

    /// <summary>Sends the form, which must be accepted; the 「已送出」 page.</summary>
    private async Task<string> SendAsync(string slug, Dictionary<string, string> fields)
    {
        var response = await _client.PostAsync($"/reserve/{slug}", Form(fields), CancellationToken);
        var html = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, html);
        return html;
    }

    /// <summary>A form for <paramref name="date"/> at <paramref name="time"/> (<c>HH:mm</c> on that date, or a full form
    /// value), a fresh key unless one is given.</summary>
    private static Dictionary<string, string> Fields(
        DateOnly date,
        string? time,
        string? key = null,
        string party = "2",
        string name = "陳小姐",
        string phone = "0912-345-678",
        string? email = null,
        string? note = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["key"] = key ?? CoordinatorReservation.NewSubmissionKey(),
            ["date"] = Value(date),
            ["party"] = party,
            ["name"] = name,
            ["phone"] = phone,
            ["website"] = string.Empty,
        };
        if (time is not null)
        {
            fields["time"] = time.Contains('T', StringComparison.Ordinal) ? time : $"{Value(date)}T{time}";
        }

        if (email is not null)
        {
            fields["email"] = email;
        }

        if (note is not null)
        {
            fields["note"] = note;
        }

        return fields;
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new(fields);

    private static string Value(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string KeyOf(string html) => Regex.Match(html, "name=\"key\" value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>The values of the time menu's options (not the placeholder).</summary>
    private static List<string> TimesOf(string html)
    {
        var menu = Regex.Match(html, "<select id=\"time\".*?</select>", RegexOptions.Singleline).Value;
        return [.. Regex.Matches(menu, "<option value=\"([^\"]+)\"").Select(match => match.Groups[1].Value)];
    }

    private async Task<CoordinatorCommand> ConfirmedCommandAsync(Organization shop, ShopScenario scenario, DateOnly date, TimeOnly? time) =>
        await CommandAsync(shop, scenario, date, time, confirm: true);

    /// <summary>A 店休 command of <paramref name="shop"/>, previewed — and confirmed when <paramref name="confirm"/>.</summary>
    private async Task<CoordinatorCommand> CommandAsync(
        Organization shop, ShopScenario scenario, DateOnly date, TimeOnly? time, bool confirm, CoordinatorCommand? reopens = null)
    {
        var lineUserId = "U" + Guid.NewGuid().ToString("N");
        var now = _host.Clock.GetUtcNow();
        var command = CoordinatorCommand.Preview(
            shop.Id, lineUserId, scenario, date, time, scenario == ShopScenario.Reopen ? null : "公告", now, reopens?.Id);
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        dbContext.CoordinatorCommands.Add(command);
        await dbContext.SaveChangesAsync(CancellationToken);
        if (confirm)
        {
            await dbContext.CoordinatorCommands.Where(candidate => candidate.Id == command.Id).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, CoordinatorCommandStatus.Confirmed)
                    .SetProperty(candidate => candidate.ConfirmedAt, now)
                    .SetProperty(candidate => candidate.ConfirmedByLineUserId, lineUserId),
                CancellationToken);
        }

        return command;
    }

    private async Task<List<CoordinatorReservation>> ReservationsAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorReservations.AsNoTracking().OrderBy(reservation => reservation.CreatedAt).ToListAsync(CancellationToken);
    }

    /// <summary>Every reservation of the host's database, with the organization filter off (tests only).</summary>
    private async Task<List<CoordinatorReservation>> ReservationsAsync()
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        return await dbContext.CoordinatorReservations.IgnoreQueryFilters().AsNoTracking().ToListAsync(CancellationToken);
    }

    private async Task<List<OrganizationActivity>> ActivitiesAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.OrganizationActivities.AsNoTracking()
            .Where(activity => activity.Action == OrganizationActivityAction.CoordinatorShopSettingsUpdated)
            .ToListAsync(CancellationToken);
    }

    private async Task<(int Exit, string Output, string Error)> RunShopAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CoordinatorShopCommand.RunAsync(_host.Factory.Services, args, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<(int Exit, string Output, string Error)> RunShopWithFileAsync(string content, params string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), $"coordinator-shop-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, content, CancellationToken);
        try
        {
            return await RunShopAsync([.. args, "--file", path]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<(int Exit, string Output, string Error)> RunReservationsAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CoordinatorReservationsCommand.RunAsync(_host.Factory.Services, args, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }
}

/// <summary>The reservation tests' host: every log entry captured on demand, and an Email sender whose
/// <see cref="IEmailSender.IsConfigured"/> a test can switch off (and must switch on again).</summary>
public sealed class CoordinatorReservationHostFixture : AuthHostFixture
{
    public CoordinatorTestLog Log { get; } = new();

    public SwitchableEmailSender Email { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(Log);
        services.AddSingleton<IEmailSender>(Email);
        services.Configure<LoggerFilterOptions>(options =>
            options.Rules.Add(new LoggerFilterRule(typeof(CoordinatorTestLog).FullName, categoryName: null, LogLevel.Trace, filter: null)));
    }
}

/// <summary>An <see cref="IEmailSender"/> that sends nothing; configured unless a test says otherwise.</summary>
public sealed class SwitchableEmailSender : IEmailSender
{
    public bool IsConfigured { get; set; } = true;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}
