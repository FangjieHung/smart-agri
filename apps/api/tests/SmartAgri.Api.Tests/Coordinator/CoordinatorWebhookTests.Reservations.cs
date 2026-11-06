using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// The members' side of online reservations (store-reservations design Slice R2, #390) through the real reservation page,
/// background jobs (driven by <see cref="Api.Jobs.JobRunner"/> at the host's clock), webhook and PostgreSQL, with the fake
/// LINE server: a new reservation's card is pushed to every member of its shop (one retry key per member, a member's
/// failure does not stop the others, the monthly limit leaves it 「未通知成員」 without retrying), 〔接受〕／〔婉拒〕 are a
/// compare-and-set only a member of that shop can win once, a reservation nobody handled expires
/// <c>pendingExpiryMinutes</c> before its time, 「訂位」 lists the next 7 days, every accepted push is in the push log,
/// and <c>coordinator-quota</c> reports the account's use — never the customer's details in a log.
/// </summary>
public sealed partial class CoordinatorWebhookTests
{
    /// <summary>Monday 2026-11-09 10:00 in Taipei.</summary>
    private static readonly DateTimeOffset ReservationNow = new(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(8));

    /// <summary>Thursday 2026-11-12: open for lunch and dinner (<see cref="CommandShopFile"/>).</summary>
    private static readonly DateOnly ReservationDay = new(2026, 11, 12);

    /// <summary>Saturday 2026-11-14: dinner until 01:00 (Sunday).</summary>
    private static readonly DateOnly OvernightDay = new(2026, 11, 14);

    private static readonly TimeZoneInfo ReservationZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    // --- The card -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reservation_from_the_page_pushes_one_card_to_every_member_and_is_marked_sent()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, slug) = await ReservationShopAsync("卡片麵店");
        var (_, otherOwner, _, _) = await ReservationShopAsync("隔壁麵店");

        var page = await _client.GetStringAsync($"/reserve/{slug}?date=2026-11-12", CancellationToken);
        var key = Regex.Match(page, "name=\"key\" value=\"([^\"]+)\"").Groups[1].Value;
        var sent = await _client.PostAsync(
            $"/reserve/{slug}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["key"] = key, ["date"] = "2026-11-12", ["time"] = "2026-11-12T18:30", ["party"] = "4", ["name"] = "陳小姐",
                ["phone"] = "0912-345-678", ["note"] = "需要兒童椅", ["website"] = string.Empty,
            }),
            CancellationToken);
        sent.StatusCode.ShouldBe(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync(CancellationToken));
        var stored = (await ReservationsOfAsync(shop)).ShouldHaveSingleItem();
        stored.MembersNotified.ShouldBe(CoordinatorReservationNotice.Pending);
        var jobs = await ReservationJobsAsync(shop);
        jobs.Select(job => job.Kind).ShouldBe([CoordinatorNotifyReservationJob.Kind, CoordinatorExpireReservationJob.Kind], ignoreOrder: true);
        jobs.Single(job => job.Kind == CoordinatorExpireReservationJob.Kind).RunAfter.ShouldBe(stored.ReservedAt.AddMinutes(-60));

        await RunJobsAsync();

        foreach (var member in new[] { owner, manager })
        {
            var card = ReservationCard(CardsTo(member).ShouldHaveSingleItem());
            card.AltText.ShouldBe("新訂位：卡片麵店 11/12（四） 18:30 4 位");
            card.Header.ShouldBe(["新訂位｜卡片麵店"]);
            card.Body.ShouldBe(["11/12（四） 18:30", "4 位", "姓名", "陳小姐", "電話", "0912-345-678", "Email", "沒有留 Email", "備註", "需要兒童椅"]);
            card.Buttons.ShouldBe(
            [
                ("撥打電話", "uri", "tel:0912345678"),
                ("接受", "postback", CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, stored.Id)),
                ("婉拒", "postback", CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, stored.Id)),
            ]);
        }

        CardsTo(otherOwner).ShouldBeEmpty("a member of another shop gets nothing");
        var keys = new[] { owner, manager }.Select(member => Pushes(member).ShouldHaveSingleItem().Headers["X-Line-Retry-Key"]).ToList();
        keys.Distinct().Count().ShouldBe(2, "one retry key per member");
        var members = await MembersAsync(shop);
        keys.ShouldBe(
            [.. new[] { owner, manager }.Select(user => CoordinatorNotifyReservationHandler.CardRetryKey(stored.Id, members.Single(member => member.LineUserId == user).Id).ToString())]);
        (await ReservationsOfAsync(shop)).ShouldHaveSingleItem().MembersNotified.ShouldBe(CoordinatorReservationNotice.Sent);
        (await PushLogsAsync(shop)).Select(log => (log.Purpose, log.Recipients)).ShouldBe(
            [(CoordinatorPushPurpose.Reservation, 1), (CoordinatorPushPurpose.Reservation, 1)]);
        (await ReservationJobsAsync(shop)).Single(job => job.Kind == CoordinatorNotifyReservationJob.Kind).Status.ShouldBe(BackgroundJobStatus.Succeeded);
    }

    [Fact]
    public async Task A_seeded_reservation_shows_its_Email_and_a_member_LINE_refuses_does_not_stop_the_others()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("拒收麵店");
        var third = NewUser();
        await BindMemberAsync(shop, third, CoordinatorRole.Manager);
        Line.ScriptPushTo(Bot.AccessToken, owner, LineBehavior.Answer(HttpStatusCode.BadRequest, """{"message":"Failed to send messages"}"""));
        try
        {
            var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(12, 0), email: "chen@example.com", note: null);
            await RunJobsAsync();

            CardsTo(owner).ShouldBeEmpty();
            Pushes(owner).Count.ShouldBe(1, "a refusal is not retried");
            foreach (var member in new[] { manager, third })
            {
                var card = ReservationCard(CardsTo(member).ShouldHaveSingleItem());
                card.Body.ShouldContain("chen@example.com");
                card.Body.ShouldNotContain("備註", "no note, no line");
            }

            (await ReservationOfAsync(shop, reservation.Id)).MembersNotified.ShouldBe(CoordinatorReservationNotice.Sent);
            (await PushLogsAsync(shop)).Count.ShouldBe(2);
        }
        finally
        {
            Line.ScriptPushTo(Bot.AccessToken, owner, null);
        }
    }

    [Fact]
    public async Task A_member_LINE_could_not_reach_is_retried_with_the_same_key_and_nobody_gets_two_cards()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("重試麵店");
        Line.ScriptPushTo(Bot.AccessToken, manager, LineBehavior.ServerError);
        try
        {
            var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 0));
            await RunJobsAsync();

            CardsTo(owner).Count.ShouldBe(1);
            CardsTo(manager).ShouldBeEmpty();
            (await ReservationOfAsync(shop, reservation.Id)).MembersNotified.ShouldBe(CoordinatorReservationNotice.Sent, "one member has it");
            var job = (await ReservationJobsAsync(shop)).Single(row => row.Kind == CoordinatorNotifyReservationJob.Kind);
            (job.Status, job.Attempts).ShouldBe((BackgroundJobStatus.Queued, 1), "the manager is tried again");

            Line.ScriptPushTo(Bot.AccessToken, manager, null);
            _host.Clock.Advance(TimeSpan.FromSeconds(31));
            await RunJobsAsync();

            CardsTo(owner).Count.ShouldBe(1, "the owner's second push was LINE's 409 for the same key");
            Pushes(owner).Count.ShouldBe(2);
            Pushes(owner).Select(push => push.Headers["X-Line-Retry-Key"]).Distinct().ShouldHaveSingleItem();
            CardsTo(manager).Count.ShouldBe(1);
            Pushes(manager).Select(push => push.Headers["X-Line-Retry-Key"]).Distinct().ShouldHaveSingleItem();
            (await PushLogsAsync(shop)).Count.ShouldBe(2, "the 409 is not counted twice");
            (await ReservationJobsAsync(shop)).Single(row => row.Kind == CoordinatorNotifyReservationJob.Kind).Status.ShouldBe(BackgroundJobStatus.Succeeded);
        }
        finally
        {
            Line.ScriptPushTo(Bot.AccessToken, manager, null);
        }
    }

    [Fact]
    public async Task Nobody_reached_after_the_last_attempt_and_nobody_to_reach_are_not_sent()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("斷線麵店");
        Line.ScriptPushTo(Bot.AccessToken, owner, LineBehavior.TimeOut);
        Line.ScriptPushTo(Bot.AccessToken, manager, LineBehavior.TimeOut);
        try
        {
            var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(19, 0));
            await RunJobsAsync();
            foreach (var wait in new[] { 31, 61, 121, 241 })
            {
                (await ReservationOfAsync(shop, reservation.Id)).MembersNotified.ShouldBe(CoordinatorReservationNotice.Pending);
                _host.Clock.Advance(TimeSpan.FromSeconds(wait));
                await RunJobsAsync();
            }

            var job = (await ReservationJobsAsync(shop)).Single(row => row.Kind == CoordinatorNotifyReservationJob.Kind);
            (job.Status, job.Attempts).ShouldBe((BackgroundJobStatus.Succeeded, 5), "the last attempt gives up without failing");
            (await ReservationOfAsync(shop, reservation.Id)).MembersNotified.ShouldBe(CoordinatorReservationNotice.NotSent);
            Pushes(owner).Count.ShouldBe(5);
            Pushes(owner).Select(push => push.Headers["X-Line-Retry-Key"]).Distinct().ShouldHaveSingleItem();
        }
        finally
        {
            Line.ScriptPushTo(Bot.AccessToken, owner, null);
            Line.ScriptPushTo(Bot.AccessToken, manager, null);
        }

        // A shop without members: nobody to tell.
        var lonely = await _host.CreateOrganizationAsync("沒人店");
        await ImportReservationShopAsync(lonely, "沒人店");
        var alone = await SeedReservationAsync(lonely, ReservationDay, new TimeOnly(12, 30));
        await RunJobsAsync();
        (await ReservationOfAsync(lonely, alone.Id)).MembersNotified.ShouldBe(CoordinatorReservationNotice.NotSent);
    }

    [Fact]
    public async Task The_monthly_limit_keeps_the_reservation_marks_it_not_sent_logs_a_warning_and_is_not_retried()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("額滿麵店");
        Line.Script(Bot.AccessToken, FakeLineServer.Push, LineBehavior.MonthlyLimit);
        _host.Log.Start();
        try
        {
            var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30), name: "林先生", phone: "0987654321", email: "lin@example.com", note: "靠窗");
            await RunJobsAsync();
            _host.Clock.Advance(TimeSpan.FromHours(1));
            await RunJobsAsync();
            _host.Log.Stop();

            var row = await ReservationOfAsync(shop, reservation.Id);
            (row.Status, row.MembersNotified).ShouldBe((CoordinatorReservationStatus.Pending, CoordinatorReservationNotice.NotSent));
            Pushes(owner).Count.ShouldBe(1, "refused once, never retried");
            Pushes(manager).ShouldBeEmpty("LINE refuses every push this month: the others are not tried");
            var job = (await ReservationJobsAsync(shop)).Single(job => job.Kind == CoordinatorNotifyReservationJob.Kind);
            (job.Status, job.Attempts).ShouldBe((BackgroundJobStatus.Succeeded, 1));
            (await PushLogsAsync(shop)).ShouldBeEmpty();

            var lines = _host.Log.Lines;
            lines.ShouldContain(line => line.Contains("monthly message limit is reached", StringComparison.Ordinal) && line.Contains(reservation.Id.ToString(), StringComparison.Ordinal));
            foreach (var secret in new[] { "林先生", "0987654321", "0987-654-321", "lin@example.com", "靠窗", owner, manager })
            {
                lines.ShouldNotContain(line => line.Contains(secret, StringComparison.Ordinal), secret);
            }

            var list = await RunReservationsCliAsync("list", "--organization", shop.Code);
            list.Exit.ShouldBe(CoordinatorReservationsCommand.ExitSuccess, list.Error);
            list.Output.ShouldContain("注意：其中 1 筆「未通知成員」");
            list.Output.ShouldContain("｜未通知成員｜");
        }
        finally
        {
            _host.Log.Stop();
            Line.ResetScripts(Bot.AccessToken);
        }
    }

    // --- 〔接受〕／〔婉拒〕 -----------------------------------------------------------------------------

    [Fact]
    public async Task The_first_press_decides_and_later_presses_are_told_who_did_what_when()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("接受麵店");
        Line.SetProfile(Bot.AccessToken, owner, "王老闆");
        var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30));
        await RunJobsAsync();
        var card = ReservationCard(CardsTo(manager).ShouldHaveSingleItem());

        var decided = (await PostbackForMessagesAsync(owner, card.Data("接受"))).ShouldHaveSingleItem();
        Said(decided).ShouldBe("已接受：陳小姐 11/12（四） 18:30 4 位。顧客電話 0912-345-678。");
        Texts(decided.GetProperty("contents").GetProperty("body")).ShouldBe(["已接受：陳小姐 11/12（四） 18:30 4 位。顧客電話 0912-345-678。"]);
        Elements(decided.GetProperty("contents").GetProperty("footer"))
            .Where(element => element.TryGetProperty("uri", out _))
            .Select(element => element.GetProperty("uri").GetString())
            .ShouldBe(["tel:0912345678"]);

        var members = await MembersAsync(shop);
        var row = await ReservationOfAsync(shop, reservation.Id);
        (row.Status, row.Version, row.HandledByMemberId).ShouldBe((CoordinatorReservationStatus.Accepted, 2, members.Single(member => member.LineUserId == owner).Id));
        row.HandledAt.ShouldNotBeNull();
        row.StatusChangedAt.ShouldBe(row.HandledAt);
        var at = ShopAnnouncements.FormatDateTime(row.HandledAt!.Value, ReservationZone);

        (await PostbackTextAsync(manager, card.Data("婉拒"))).ShouldBe($"這筆訂位已由王老闆在 {at} 接受。");
        (await PostbackTextAsync(owner, card.Data("接受"))).ShouldBe($"這筆訂位已由你在 {at} 接受。");
        (await ReservationOfAsync(shop, reservation.Id)).Version.ShouldBe(2, "nothing changed");

        // Declined by the manager, whose LINE name the account cannot get: 「店長」.
        var second = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(19, 0), phone: "0911222333");
        Said((await PostbackForMessagesAsync(manager, CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, second.Id))).ShouldHaveSingleItem())
            .ShouldBe("已婉拒：陳小姐 11/12（四） 19:00 4 位。顧客電話 0911-222-333。");
        var declined = await ReservationOfAsync(shop, second.Id);
        declined.Status.ShouldBe(CoordinatorReservationStatus.Declined);
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, second.Id)))
            .ShouldBe($"這筆訂位已由店長在 {ShopAnnouncements.FormatDateTime(declined.HandledAt!.Value, ReservationZone)} 婉拒。");
        await RunJobsAsync();
    }

    [Fact]
    public async Task Two_members_pressing_at_the_same_moment_decide_once()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("同時麵店");
        var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30));
        await RunJobsAsync();

        var posts = await Task.WhenAll(
            PostAsync(Body(Bot.UserId, Postback(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, reservation.Id), "rt-reserve-1")), sign: true),
            PostAsync(Body(Bot.UserId, Postback(manager, CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, reservation.Id), "rt-reserve-2")), sign: true));
        posts.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        var replies = new[] { Said((await ReplyMessagesAsync("rt-reserve-1"))[0]), Said((await ReplyMessagesAsync("rt-reserve-2"))[0]) };
        replies.Count(reply => reply.StartsWith("已接受：", StringComparison.Ordinal) || reply.StartsWith("已婉拒：", StringComparison.Ordinal)).ShouldBe(1);
        replies.Count(reply => reply.StartsWith("這筆訂位已由", StringComparison.Ordinal)).ShouldBe(1);
        var row = await ReservationOfAsync(shop, reservation.Id);
        row.Version.ShouldBe(2);
        row.Status.ShouldBeOneOf(CoordinatorReservationStatus.Accepted, CoordinatorReservationStatus.Declined);

        // The compare-and-set itself: of many at once, exactly one wins, and the seam runs once, in its transaction.
        var other = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(19, 30), phone: "0922333444");
        var recorder = new RecordingOutcomes();
        using var optionsScope = _host.Factory.Services.CreateScope();
        var store = new CoordinatorReservations(optionsScope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), recorder);
        var memberId = (await MembersAsync(shop)).First().Id;
        var presses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => store.DecideAsync(
            shop.Id,
            other.Id,
            version: 1,
            index % 2 == 0 ? CoordinatorReservationStatus.Accepted : CoordinatorReservationStatus.Declined,
            memberId,
            _host.Clock.GetUtcNow(),
            CancellationToken)));
        presses.Count(press => press is not null).ShouldBe(1);
        recorder.Calls.ShouldBe(1);
        (await ReservationJobsAsync(shop)).Count(job => job.Kind == RecordingOutcomes.Kind).ShouldBe(1, "what the seam added committed with the change");
        await RunJobsAsync();
    }

    [Fact]
    public async Task Only_a_member_of_that_shop_can_press_and_cancelled_or_unknown_reservations_say_so()
    {
        SetNow(ReservationNow);
        var (shop, owner, _, _) = await ReservationShopAsync("本店");
        var (other, otherOwner, _, _) = await ReservationShopAsync("他店");
        var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30));
        await RunJobsAsync();
        var accept = CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, reservation.Id);

        (await PostbackTextAsync(otherOwner, accept)).ShouldBe(CoordinatorReservationCards.NotAMember);
        (await PostbackTextAsync(otherOwner, CoordinatorMessageRouter.AcceptReservationPostback(other.Id, reservation.Id)))
            .ShouldBe(CoordinatorReservationCards.Unknown, "another shop's reservation is not found in the sender's shop");
        (await PostbackTextAsync(NewUser(), accept)).ShouldBe(CoordinatorReplies.AskForCode);
        var untouched = await ReservationOfAsync(shop, reservation.Id);
        (untouched.Status, untouched.Version).ShouldBe((CoordinatorReservationStatus.Pending, 1));

        await using (var dbContext = _host.Postgres.CreateDbContext(shop.Id))
        {
            await dbContext.CoordinatorReservations.Where(row => row.Id == reservation.Id).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, CoordinatorReservationStatus.Cancelled)
                    .SetProperty(row => row.CancelReason, ShopScenario.TemporaryClosure)
                    .SetProperty(row => row.StatusChangedAt, _host.Clock.GetUtcNow())
                    .SetProperty(row => row.Version, 2),
                CancellationToken);
        }

        (await PostbackTextAsync(owner, accept)).ShouldBe(CoordinatorReservationCards.Cancelled);
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, Guid.CreateVersion7())))
            .ShouldBe(CoordinatorReservationCards.Unknown);
    }

    // --- Expiry ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reservation_nobody_handled_expires_60_minutes_before_its_time_also_after_midnight()
    {
        SetNow(ReservationNow);
        var (shop, owner, _, _) = await ReservationShopAsync("過期麵店");
        var dinner = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30));
        var late = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(19, 0), phone: "0933444555");
        var night = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(0, 30), phone: "0944555666");
        await RunJobsAsync();

        night.Date.ShouldBe(OvernightDay);
        night.ReservedAt.ShouldBe(new DateTimeOffset(2026, 11, 15, 0, 30, 0, TimeSpan.FromHours(8)));
        night.When(ReservationZone).ShouldBe("11/14（六） 00:30（隔天）");
        var expiries = (await ReservationJobsAsync(shop)).Where(job => job.Kind == CoordinatorExpireReservationJob.Kind).Select(job => job.RunAfter).Order().ToList();
        expiries.ShouldBe(
        [
            new DateTimeOffset(2026, 11, 12, 17, 30, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 11, 12, 18, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 11, 14, 23, 30, 0, TimeSpan.FromHours(8)),
        ]);

        SetNow(new DateTimeOffset(2026, 11, 12, 17, 29, 0, TimeSpan.FromHours(8)));
        await RunJobsAsync();
        (await ReservationOfAsync(shop, dinner.Id)).Status.ShouldBe(CoordinatorReservationStatus.Pending);

        SetNow(new DateTimeOffset(2026, 11, 12, 17, 30, 5, TimeSpan.FromHours(8)));
        await RunJobsAsync();
        var expired = await ReservationOfAsync(shop, dinner.Id);
        (expired.Status, expired.Version, expired.HandledByMemberId).ShouldBe((CoordinatorReservationStatus.Expired, 2, (Guid?)null));
        expired.StatusChangedAt.ShouldNotBeNull();
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, dinner.Id)))
            .ShouldBe("這筆訂位已過期：訂位時間前 60 分鐘還沒有人處理。");
        (await ReservationOfAsync(shop, late.Id)).Status.ShouldBe(CoordinatorReservationStatus.Pending);

        // Pressed after its expiry, before its job ran: it expires then.
        SetNow(new DateTimeOffset(2026, 11, 12, 18, 0, 5, TimeSpan.FromHours(8)));
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, late.Id)))
            .ShouldBe("這筆訂位已過期：訂位時間前 60 分鐘還沒有人處理。");
        (await ReservationOfAsync(shop, late.Id)).Status.ShouldBe(CoordinatorReservationStatus.Expired);
        await RunJobsAsync();
        (await ReservationOfAsync(shop, late.Id)).Version.ShouldBe(2, "the job found it expired already");

        // 00:30 after Saturday's midnight expires at Saturday 23:30.
        SetNow(new DateTimeOffset(2026, 11, 14, 23, 29, 0, TimeSpan.FromHours(8)));
        await RunJobsAsync();
        (await ReservationOfAsync(shop, night.Id)).Status.ShouldBe(CoordinatorReservationStatus.Pending);
        SetNow(new DateTimeOffset(2026, 11, 14, 23, 30, 5, TimeSpan.FromHours(8)));
        await RunJobsAsync();
        (await ReservationOfAsync(shop, night.Id)).Status.ShouldBe(CoordinatorReservationStatus.Expired);
    }

    [Fact]
    public async Task An_accepted_reservation_does_not_expire_and_a_lowered_expiry_is_rescheduled()
    {
        SetNow(ReservationNow);
        var (shop, owner, _, _) = await ReservationShopAsync("改設定麵店");
        var accepted = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30));
        var pending = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(19, 0), phone: "0955666777");
        await RunJobsAsync();
        Said((await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, accepted.Id)))[0]).ShouldStartWith("已接受：");

        // The shop now lets reservations wait until 30 minutes before their time.
        await ImportReservationShopAsync(shop, "改設定麵店", pendingExpiryMinutes: 30);
        SetNow(new DateTimeOffset(2026, 11, 12, 18, 0, 5, TimeSpan.FromHours(8)));
        await RunJobsAsync();
        (await ReservationOfAsync(shop, accepted.Id)).Status.ShouldBe(CoordinatorReservationStatus.Accepted);
        (await ReservationOfAsync(shop, pending.Id)).Status.ShouldBe(CoordinatorReservationStatus.Pending, "due at 18:30 now");
        (await ReservationJobsAsync(shop)).Count(job => job.Kind == CoordinatorExpireReservationJob.Kind && job.Status == BackgroundJobStatus.Queued)
            .ShouldBe(1);

        SetNow(new DateTimeOffset(2026, 11, 12, 18, 30, 5, TimeSpan.FromHours(8)));
        await RunJobsAsync();
        (await ReservationOfAsync(shop, pending.Id)).Status.ShouldBe(CoordinatorReservationStatus.Expired);
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, pending.Id)))
            .ShouldBe("這筆訂位已過期：訂位時間前 30 分鐘還沒有人處理。");
    }

    // --- 「訂位」 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Reservations_lists_the_pending_and_accepted_ones_of_the_next_7_days_earliest_first()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("清單麵店");
        Said((await SendForMessagesAsync(owner, "訂位")).ShouldHaveSingleItem()).ShouldBe("清單麵店：接下來 7 天沒有訂位。");

        var thursday = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30), phone: "0910000001");
        var tuesday = await SeedReservationAsync(shop, new DateOnly(2026, 11, 10), new TimeOnly(12, 0), phone: "0910000002");
        var declined = await SeedReservationAsync(shop, new DateOnly(2026, 11, 13), new TimeOnly(12, 0), phone: "0910000003");
        var tonight = await SeedReservationAsync(shop, new DateOnly(2026, 11, 9), new TimeOnly(18, 0), phone: "0910000004");
        await SeedReservationAsync(shop, new DateOnly(2026, 11, 16), new TimeOnly(12, 0), phone: "0910000005");
        await RunJobsAsync();
        Said((await PostbackForMessagesAsync(manager, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, tuesday.Id)))[0]).ShouldStartWith("已接受：");
        Said((await PostbackForMessagesAsync(manager, CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, declined.Id)))[0]).ShouldStartWith("已婉拒：");

        foreach (var text in new[] { "訂位", "　查看訂位 " })
        {
            var message = (await SendForMessagesAsync(owner, text)).ShouldHaveSingleItem();
            Said(message).ShouldBe("清單麵店 接下來 7 天的訂位：待處理 2 筆、已接受 1 筆。");
            var bubbles = Bubbles(message);
            bubbles.Select(bubble => bubble.Header[1]).ShouldBe(["11/9（一） 18:00", "11/10（二） 12:00", "11/12（四） 18:30"]);
            bubbles.Select(bubble => bubble.Header[2]).ShouldBe(["待處理", "已接受", "待處理"]);
            bubbles[0].Buttons.Select(button => button.Value).ShouldBe(
            [
                "tel:0910000004",
                CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, tonight.Id),
                CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, tonight.Id),
            ]);
            bubbles[1].Buttons.Select(button => button.Label).ShouldBe(["撥打電話"], "an accepted one has no decision buttons");
            bubbles[2].Buttons[1].Value.ShouldBe(CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, thursday.Id));
            bubbles[2].Body.ShouldContain("chen@example.com");
        }

        // On Sunday at 00:10 the small hours of Saturday's business day are still ahead.
        var night = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(0, 30), phone: "0910000006");
        var saturdayDinner = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(20, 0), phone: "0910000007");
        using var storeScope = _host.Factory.Services.CreateScope();
        var store = storeScope.ServiceProvider.GetRequiredService<CoordinatorReservations>();
        var memberId = (await MembersAsync(shop)).First().Id;
        foreach (var reservation in new[] { night, saturdayDinner })
        {
            (await store.DecideAsync(shop.Id, reservation.Id, 1, CoordinatorReservationStatus.Accepted, memberId, _host.Clock.GetUtcNow(), CancellationToken))
                .ShouldNotBeNull();
        }

        SetNow(new DateTimeOffset(2026, 11, 15, 0, 10, 0, TimeSpan.FromHours(8)));
        var sunday = (await SendForMessagesAsync(owner, "訂位")).ShouldHaveSingleItem();
        var sundayBubbles = Bubbles(sunday);
        sundayBubbles.Select(bubble => bubble.Header[1]).ShouldContain("11/14（六） 00:30（隔天）");
        sundayBubbles.Select(bubble => bubble.Header[1]).ShouldNotContain("11/14（六） 20:00");
        await RunJobsAsync();
    }

    [Fact]
    public async Task Reservations_shows_the_first_12_says_how_many_more_and_asks_a_member_of_several_shops_which_one()
    {
        SetNow(ReservationNow);
        var (busy, owner, _, _) = await ReservationShopAsync("乙店" + Guid.NewGuid().ToString("N")[..4]);
        for (var index = 0; index < 14; index++)
        {
            await SeedReservationAsync(
                busy, ReservationDay.AddDays(index / 7), new TimeOnly(11, 0).AddMinutes(30 * (index % 7)), phone: $"09200000{index:D2}");
        }

        await RunJobsAsync();
        var messages = await SendForMessagesAsync(owner, "訂位");
        messages.Count.ShouldBe(2);
        Bubbles(messages[0]).Count.ShouldBe(CoordinatorReservationCards.CarouselMaxBubbles);
        Said(messages[1]).ShouldBe("另外還有 2 筆訂位沒有列出（只列最早的 12 筆）。");

        var (quiet, _, _, _) = await ReservationShopAsync("甲店" + Guid.NewGuid().ToString("N")[..4]);
        await BindMemberAsync(quiet, owner, CoordinatorRole.Manager);
        var choice = (await SendForMessagesAsync(owner, "訂位")).ShouldHaveSingleItem();
        Said(choice).ShouldBe(CoordinatorReservationCards.ChooseShop);
        QuickReplyActions(choice).Select(action => action.GetProperty("data").GetString()).ShouldBe(
            [CoordinatorMessageRouter.ReservationsPostback(busy.Id), CoordinatorMessageRouter.ReservationsPostback(quiet.Id)], ignoreOrder: true);

        Said((await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.ReservationsPostback(quiet.Id))).ShouldHaveSingleItem())
            .ShouldEndWith("：接下來 7 天沒有訂位。");
        (await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.ReservationsPostback(busy.Id))).Count.ShouldBe(2);
        var (stranger, _, _, _) = await ReservationShopAsync("丙店");
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.ReservationsPostback(stranger.Id))).ShouldBe(CoordinatorReservationCards.NotAMember);
    }

    // --- The push log and coordinator-quota -----------------------------------------------------------

    [Fact]
    public async Task Every_push_LINE_accepts_is_in_the_push_log_once_and_refusals_are_not()
    {
        SetNow(ReservationNow);
        var shop = await _host.CreateOrganizationAsync("推播紀錄店");
        var user = NewUser();
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<CoordinatorReplySender>();
        var message = new[] { LineMessages.Text("測試") };

        foreach (var purpose in Enum.GetValues<CoordinatorPushPurpose>())
        {
            (await sender.PushAsync(shop.Id, user, message, Guid.NewGuid(), purpose, CancellationToken)).Outcome.ShouldBe(CoordinatorPushOutcome.Sent);
        }

        var key = Guid.NewGuid();
        (await sender.PushAsync(shop.Id, user, message, key, CoordinatorPushPurpose.Reminder, CancellationToken)).Outcome.ShouldBe(CoordinatorPushOutcome.Sent);
        (await sender.PushAsync(shop.Id, user, message, key, CoordinatorPushPurpose.Reminder, CancellationToken)).Outcome.ShouldBe(CoordinatorPushOutcome.AlreadySent);
        try
        {
            foreach (var (behavior, outcome) in new[]
                     {
                         (LineBehavior.ServerError, CoordinatorPushOutcome.Failed),
                         (LineBehavior.RateLimited, CoordinatorPushOutcome.Failed),
                         (LineBehavior.TimeOut, CoordinatorPushOutcome.Failed),
                         (LineBehavior.MonthlyLimit, CoordinatorPushOutcome.MonthlyLimit),
                         (LineBehavior.Answer(HttpStatusCode.BadRequest, """{"message":"Failed to send messages"}"""), CoordinatorPushOutcome.Refused),
                         (LineBehavior.Forbidden, CoordinatorPushOutcome.Refused),
                     })
            {
                Line.ScriptPushTo(Bot.AccessToken, user, behavior);
                (await sender.PushAsync(shop.Id, user, message, Guid.NewGuid(), CoordinatorPushPurpose.FollowUp, CancellationToken)).Outcome.ShouldBe(outcome);
            }
        }
        finally
        {
            Line.ScriptPushTo(Bot.AccessToken, user, null);
        }

        var logs = await PushLogsAsync(shop);
        logs.Select(log => log.Purpose).ShouldBe(
            [CoordinatorPushPurpose.Reservation, CoordinatorPushPurpose.Reminder, CoordinatorPushPurpose.FollowUp, CoordinatorPushPurpose.Reauthorization, CoordinatorPushPurpose.Reminder],
            ignoreOrder: true);
        logs.ShouldAllBe(log => log.Recipients == 1 && log.OrganizationId == shop.Id);
        logs.ShouldAllBe(log => log.SentAt > ReservationNow.AddMinutes(-1) && log.SentAt < ReservationNow.AddMinutes(5));
    }

    [Fact]
    public async Task Coordinator_quota_prints_lines_quota_and_this_months_pushes_per_shop_without_customer_data()
    {
        SetNow(ReservationNow);
        var (shop, _, _, _) = await ReservationShopAsync("額度麵店");
        var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30), name: "黃小姐", phone: "0966777888", email: "huang@example.com", note: "生日");
        await RunJobsAsync();
        await using (var scope = _host.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<CoordinatorReplySender>()
                .PushAsync(shop.Id, NewUser(), [LineMessages.Text("提醒")], Guid.NewGuid(), CoordinatorPushPurpose.Reminder, CancellationToken)).Delivered.ShouldBeTrue();
        }

        await using (var dbContext = _host.Postgres.CreateDbContext(shop.Id))
        {
            // Last month's push is not this month's.
            dbContext.CoordinatorPushLogs.Add(CoordinatorPushLog.Record(
                shop.Id, CoordinatorPushPurpose.FollowUp, 1, new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.FromHours(8))));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        Line.SetQuota(Bot.AccessToken, 200);
        Line.SetConsumption(Bot.AccessToken, 37);
        try
        {
            var result = await RunQuotaAsync();
            result.Exit.ShouldBe(CoordinatorQuotaCommand.ExitSuccess, result.Error);
            result.Output.ShouldContain("統籌助理本月訊息額度：200 則（LINE 的每月上限）。");
            result.Output.ShouldContain("本月已用：37 則");
            result.Output.ShouldContain("剩餘：163 則。");
            result.Output.ShouldContain("本月（Asia/Taipei 的 2026-11）");
            result.Output.ShouldContain(
                $"組織 {shop.Code}（額度麵店）：訂位卡片 2、提醒 1、執行結果 0、重新授權連結 0，合計 3 則。本月訂位 1 筆、成員 2 人、確認的店休類指令 0 次；估算 2 則。");
            result.Output.ShouldContain("估算（設計文件第 7 節）：Σ各店（每月訂位筆數 × 成員人數）＋ Σ各店（每月店休類指令數 × 5）");
            foreach (var secret in new[] { "黃小姐", "0966777888", "0966-777-888", "huang@example.com", "生日", reservation.Id.ToString() })
            {
                (result.Output + result.Error).ShouldNotContain(secret);
            }

            Line.SetQuota(Bot.AccessToken, null);
            (await RunQuotaAsync()).Output.ShouldContain("統籌助理本月訊息額度：無上限");

            Line.SetQuota(Bot.AccessToken, 30);
            (await RunQuotaAsync()).Output.ShouldContain("剩餘：0 則。本月額度已用完");

            Line.Script(Bot.AccessToken, FakeLineServer.Quota, LineBehavior.ServerError);
            var failed = await RunQuotaAsync();
            failed.Exit.ShouldBe(CoordinatorQuotaCommand.ExitFailed);
            failed.Error.ShouldContain("無法向 LINE 查詢統籌助理的額度：HttpError（HTTP 500");
            failed.Output.ShouldContain($"組織 {shop.Code}（額度麵店）：", Case.Sensitive, "the push log is still printed");
        }
        finally
        {
            Line.ResetScripts(Bot.AccessToken);
            Line.SetQuota(Bot.AccessToken, 200);
            Line.SetConsumption(Bot.AccessToken, 0);
        }

        (await RunQuotaAsync("--help")).Output.ShouldStartWith("用法：coordinator-quota");
        (await RunQuotaAsync("--nope")).Exit.ShouldBe(CoordinatorQuotaCommand.ExitUsage);
    }

    [Fact]
    public async Task The_customers_details_never_reach_a_log()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("隱私麵店");
        _host.Log.Start();
        try
        {
            var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30), name: "吳先生", phone: "0977888999", email: "wu@example.com", note: "素食兩位");
            await RunJobsAsync();
            Said((await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, reservation.Id)))[0]).ShouldStartWith("已接受：吳先生");
            (await PostbackTextAsync(manager, CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, reservation.Id))).ShouldStartWith("這筆訂位已由");
            Said((await SendForMessagesAsync(manager, "訂位"))[0]).ShouldContain("已接受 1 筆");
        }
        finally
        {
            _host.Log.Stop();
        }

        var lines = _host.Log.Lines;
        lines.ShouldContain(line => line.Contains("card pushed to 2 of 2 members", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("accepted by member", StringComparison.Ordinal));
        foreach (var secret in new[] { "吳先生", "0977888999", "0977-888-999", "wu@example.com", "素食兩位", owner, manager })
        {
            lines.ShouldNotContain(line => line.Contains(secret, StringComparison.Ordinal), secret);
        }
    }

    // --- Helpers --------------------------------------------------------------------------------------

    /// <summary>A shop of <see cref="CommandShopFile"/> named <paramref name="name"/> taking reservations, with a bound
    /// owner and manager.</summary>
    private async Task<(Organization Shop, string Owner, string Manager, string Slug)> ReservationShopAsync(string name)
    {
        var shop = await _host.CreateOrganizationAsync(name);
        var slug = await ImportReservationShopAsync(shop, name);
        var owner = NewUser();
        var manager = NewUser();
        await BindMemberAsync(shop, owner, CoordinatorRole.Owner);
        _host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await BindMemberAsync(shop, manager, CoordinatorRole.Manager);
        return (shop, owner, manager, slug);
    }

    /// <summary>Imports <see cref="CommandShopFile"/> as <paramref name="name"/> with open reservations; the slug.</summary>
    private async Task<string> ImportReservationShopAsync(Organization shop, string name, int pendingExpiryMinutes = 60)
    {
        var tree = JsonNode.Parse(CommandShopFile)!.AsObject();
        tree["storeName"] = name;
        var slug = "r-" + shop.Code;
        tree["reservations"] = new JsonObject
        {
            ["enabled"] = true, ["slug"] = slug, ["phone"] = "02-2345-6789", ["pendingExpiryMinutes"] = pendingExpiryMinutes,
        };
        var parsed = ShopSettingsJson.Parse(tree.ToJsonString());
        parsed.Errors.ShouldBeEmpty(string.Join('\n', parsed.Errors));
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<CoordinatorShops>().ImportAsync(shop.Id, parsed.Document!, CancellationToken))
            .Outcome.ShouldBeOneOf(ShopImportOutcome.Created, ShopImportOutcome.Updated);
        return slug;
    }

    private async Task BindMemberAsync(Organization shop, string lineUserId, CoordinatorRole role)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        dbContext.CoordinatorMembers.Add(CoordinatorMember.Bind(shop.Id, lineUserId, role, _host.Clock.GetUtcNow()));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    /// <summary>A reservation of <paramref name="shop"/> on business day <paramref name="date"/> at <paramref name="time"/>,
    /// stored as the page stores it (with its jobs, expiring 60 minutes before).</summary>
    private async Task<CoordinatorReservation> SeedReservationAsync(
        Organization shop,
        DateOnly date,
        TimeOnly time,
        string name = "陳小姐",
        string phone = "0912345678",
        string? email = "chen@example.com",
        string? note = "需要兒童椅",
        int party = 4)
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var settings = (await scope.ServiceProvider.GetRequiredService<CoordinatorShops>().FindAsync(shop.Id, CancellationToken))!;
        var at = settings.Hours.OnBusinessDay(date, time);
        var slot = new ReservationSlot(date, at, TimeZoneInfo.ConvertTime(at, settings.TimeZone).DateTime);
        var now = _host.Clock.GetUtcNow();
        var reservation = CoordinatorReservation.Submit(shop.Id, slot, party, name, phone, email, note, CoordinatorReservation.NewSubmissionKey(), now);
        (await scope.ServiceProvider.GetRequiredService<CoordinatorReservations>()
                .SubmitAsync(reservation, settings.Reservations!.PendingExpiryMinutes, now, CancellationToken))
            .Outcome.ShouldBe(ReservationSubmitOutcome.Stored);
        return reservation;
    }

    /// <summary>The reservation cards LINE accepted for <paramref name="user"/> (once per retry key), in order.</summary>
    private List<JsonElement> CardsTo(string user) =>
        [.. Line.PushesOf(Bot.AccessToken)
            .Where(push => push.Json.GetProperty("to").GetString() == user)
            .Select(push => PushedMessages(push).ShouldHaveSingleItem())
            .Where(message => message.GetProperty("type").GetString() == "flex" && message.GetProperty("altText").GetString()!.StartsWith("新訂位：", StringComparison.Ordinal))];

    private static ReservationBubble ReservationCard(JsonElement message)
    {
        message.GetProperty("type").GetString().ShouldBe("flex");
        var bubble = ReservationBubble.Of(message.GetProperty("contents"));
        return bubble with { AltText = message.GetProperty("altText").GetString()! };
    }

    private static List<ReservationBubble> Bubbles(JsonElement message)
    {
        message.GetProperty("type").GetString().ShouldBe("flex");
        var carousel = message.GetProperty("contents");
        carousel.GetProperty("type").GetString().ShouldBe("carousel");
        return [.. carousel.GetProperty("contents").EnumerateArray().Select(ReservationBubble.Of)];
    }

    private async Task<CoordinatorReservation> ReservationOfAsync(Organization shop, Guid reservationId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorReservations.AsNoTracking().SingleAsync(reservation => reservation.Id == reservationId, CancellationToken);
    }

    private async Task<List<CoordinatorReservation>> ReservationsOfAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorReservations.AsNoTracking().OrderBy(reservation => reservation.ReservedAt).ToListAsync(CancellationToken);
    }

    private async Task<List<CoordinatorPushLog>> PushLogsAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorPushLogs.AsNoTracking().OrderBy(log => log.SentAt).ToListAsync(CancellationToken);
    }

    private async Task<List<BackgroundJob>> ReservationJobsAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.BackgroundJobs.AsNoTracking()
            .Where(job => job.Kind == CoordinatorNotifyReservationJob.Kind || job.Kind == CoordinatorExpireReservationJob.Kind || job.Kind == RecordingOutcomes.Kind)
            .OrderBy(job => job.CreatedAt)
            .ToListAsync(CancellationToken);
    }

    private async Task<(int Exit, string Output, string Error)> RunReservationsCliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CoordinatorReservationsCommand.RunAsync(_host.Factory.Services, args, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<(int Exit, string Output, string Error)> RunQuotaAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CoordinatorQuotaCommand.RunAsync(_host.Factory.Services, args, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>A bubble of a reservation card or list: the header's texts, the body's texts, and the buttons — label,
    /// action type, and the postback data or URI.</summary>
    private sealed record ReservationBubble(List<string> Header, List<string> Body, List<(string Label, string Type, string Value)> Buttons)
    {
        public string AltText { get; init; } = string.Empty;

        public string Data(string label) => Buttons.Single(button => button.Label == label).Value;

        public static ReservationBubble Of(JsonElement bubble) => new(
            bubble.TryGetProperty("header", out var header) ? Texts(header) : [],
            Texts(bubble.GetProperty("body")),
            [.. Elements(bubble.GetProperty("footer"))
                .Where(element => element.TryGetProperty("type", out var type) && type.GetString() == "button")
                .Select(element => element.GetProperty("action"))
                .Select(action => (
                    action.GetProperty("label").GetString()!,
                    action.GetProperty("type").GetString()!,
                    action.GetProperty("type").GetString() == "uri" ? action.GetProperty("uri").GetString()! : action.GetProperty("data").GetString()!))]);
    }

    /// <summary>An <see cref="ICoordinatorReservationOutcomes"/> that counts its calls and adds a job, like #391's will.</summary>
    private sealed class RecordingOutcomes : ICoordinatorReservationOutcomes
    {
        public const string Kind = "test.reservation-outcome";

        private int _calls;

        public int Calls => _calls;

        public void Decided(AppDbContext dbContext, CoordinatorReservation reservation, DateTimeOffset now)
        {
            Interlocked.Increment(ref _calls);
            dbContext.BackgroundJobs.Add(BackgroundJob.Create(reservation.OrganizationId, Kind, new { reservation.Id }, now, runAfter: now.AddYears(10)));
        }
    }
}
