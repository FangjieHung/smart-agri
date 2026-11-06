using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// The customer's reservation Emails (store-reservations design Slice R3, #391) through the real webhook, compare-and-set,
/// job queue (at the host's clock), <c>SmtpEmailSender</c> and the host's <see cref="FakeSmtpServer"/>: accepting,
/// declining and expiring each enqueue one job with the change and send one plain-text Email from the platform's address
/// under the shop's name, with the shop's phone; only the press that won sends one, a retried expiry none; without an
/// address or without SMTP nothing is enqueued and the members are told to call; a refused Email is retried with backoff
/// and its error carries no address.
/// </summary>
public sealed partial class CoordinatorWebhookTests
{
    private const string EmailFooter = "——\n這封信由線上訂位系統代{0}寄出；回覆這封信不會有人讀取。\n";

    private FakeSmtpServer Smtp => _host.Smtp;

    [Fact]
    public async Task Accepting_declining_and_expiring_each_Email_the_customer_once_from_the_shop_with_its_phone()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("信件麵店");
        var (acceptedTo, declinedTo, expiredTo) = (Guest(), Guest(), Guest());
        var accepted = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(0, 30), email: acceptedTo, party: 2);
        var declined = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30), phone: "0911222333", email: declinedTo);
        var expired = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(19, 0), phone: "0922333444", email: expiredTo);
        await RunJobsAsync();
        (await EmailJobsAsync(shop)).ShouldBeEmpty("nobody is told before a decision");
        CardsTo(owner).Select(ReservationCard).ShouldContain(card => card.Body.Contains(acceptedTo), "a customer who left an address is shown it");

        Said((await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, accepted.Id))).ShouldHaveSingleItem())
            .ShouldBe("已接受：陳小姐 11/14（六） 00:30（隔天） 2 位。會寄確認信給顧客。");
        Said((await PostbackForMessagesAsync(manager, CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, declined.Id))).ShouldHaveSingleItem())
            .ShouldBe("已婉拒：陳小姐 11/12（四） 18:30 4 位。會寄婉拒信給顧客。");

        var queued = await EmailJobsAsync(shop);
        queued.Select(job => (Payload(job).ReservationId, Payload(job).Outcome)).ShouldBe(
            [(accepted.Id, CoordinatorReservationStatus.Accepted), (declined.Id, CoordinatorReservationStatus.Declined)], ignoreOrder: true);
        queued.ShouldAllBe(job => job.Status == BackgroundJobStatus.Queued && job.Attempts == 0);
        Smtp.To(acceptedTo).ShouldBeEmpty("the press is answered first; the Email is the job queue's");

        await RunJobsAsync();

        var confirmation = Smtp.To(acceptedTo).ShouldHaveSingleItem();
        confirmation.Recipients.ShouldBe([acceptedTo]);
        confirmation.From.ShouldBe(CoordinatorHostFixture.SmtpFrom);
        confirmation.FromHeader.ShouldBe($"信件麵店 <{CoordinatorHostFixture.SmtpFrom}>", "the shop's name at the platform's address");
        confirmation.Subject.ShouldBe("信件麵店：訂位已確認");
        confirmation.Body.ShouldBe(
            "信件麵店已確認您的訂位：11/14（六） 00:30（隔天），2 位。如需更改或取消，請來電 02-2345-6789。\n\n" + Footer("信件麵店"));
        var regret = Smtp.To(declinedTo).ShouldHaveSingleItem();
        regret.FromHeader.ShouldBe($"信件麵店 <{CoordinatorHostFixture.SmtpFrom}>");
        regret.Subject.ShouldBe("信件麵店：無法接受您的訂位");
        regret.Body.ShouldBe("很抱歉，信件麵店無法接受您 11/12（四） 18:30 的訂位，歡迎改選其他時間或來電 02-2345-6789。\n\n" + Footer("信件麵店"));
        Smtp.To(expiredTo).ShouldBeEmpty();

        // Nobody handled the 19:00 one: it expires at 18:00, and its Email follows in the same run of the queue.
        SetNow(new DateTimeOffset(2026, 11, 12, 18, 0, 5, TimeSpan.FromHours(8)));
        await RunJobsAsync();

        (await ReservationOfAsync(shop, expired.Id)).Status.ShouldBe(CoordinatorReservationStatus.Expired);
        var lapsed = Smtp.To(expiredTo).ShouldHaveSingleItem();
        lapsed.Subject.ShouldBe("信件麵店：訂位未能確認");
        lapsed.Body.ShouldBe(
            "很抱歉，信件麵店未能及時確認您 11/12（四） 19:00 的訂位，這筆訂位已失效。如仍需訂位，請來電 02-2345-6789。\n\n" + Footer("信件麵店"));
        var done = await EmailJobsAsync(shop);
        done.Select(job => (job.Status, job.Attempts)).ShouldBe([.. Enumerable.Repeat((BackgroundJobStatus.Succeeded, 1), 3)]);
        done.Select(job => Payload(job).Outcome).ShouldBe(
            [CoordinatorReservationStatus.Accepted, CoordinatorReservationStatus.Declined, CoordinatorReservationStatus.Expired], ignoreOrder: true);
        new[] { acceptedTo, declinedTo, expiredTo }.ShouldAllBe(address => Smtp.To(address).Count == 1);
    }

    [Fact]
    public async Task Only_the_press_that_won_sends_an_Email_and_an_expiry_run_again_sends_none()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("一封麵店");
        var to = Guest();
        var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30), email: to);
        await RunJobsAsync();

        var accept = CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, reservation.Id);
        var decline = CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, reservation.Id);
        var posts = await Task.WhenAll(
            PostAsync(Body(Bot.UserId, Postback(owner, accept, "rt-email-race-1")), sign: true),
            PostAsync(Body(Bot.UserId, Postback(manager, decline, "rt-email-race-2")), sign: true));
        posts.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        var replies = new[] { Said((await ReplyMessagesAsync("rt-email-race-1"))[0]), Said((await ReplyMessagesAsync("rt-email-race-2"))[0]) };
        replies.Count(reply => reply.EndsWith("會寄確認信給顧客。", StringComparison.Ordinal) || reply.EndsWith("會寄婉拒信給顧客。", StringComparison.Ordinal))
            .ShouldBe(1);
        (await PostbackTextAsync(owner, accept)).ShouldStartWith("這筆訂位已由");
        (await PostbackTextAsync(manager, decline)).ShouldStartWith("這筆訂位已由");
        (await EmailJobsAsync(shop)).ShouldHaveSingleItem("one job, by the press that won");

        await RunJobsAsync();
        var row = await ReservationOfAsync(shop, reservation.Id);
        Smtp.To(to).ShouldHaveSingleItem().Subject.ShouldBe(row.Status == CoordinatorReservationStatus.Accepted ? "一封麵店：訂位已確認" : "一封麵店：無法接受您的訂位");

        // Many compare-and-sets at once with the real seam: one wins, one job.
        var raced = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(19, 30), phone: "0922333444", email: Guest());
        var memberId = (await MembersAsync(shop)).First().Id;
        await using (var scope = _host.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<CoordinatorReservations>();
            var presses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => store.DecideAsync(
                shop.Id,
                raced.Id,
                version: 1,
                index % 2 == 0 ? CoordinatorReservationStatus.Accepted : CoordinatorReservationStatus.Declined,
                memberId,
                _host.Clock.GetUtcNow(),
                CancellationToken)));
            presses.Count(press => press is not null).ShouldBe(1);
        }

        (await EmailJobsAsync(shop)).Count(job => Payload(job).ReservationId == raced.Id).ShouldBe(1);

        // An expiry: its job run again, the compare-and-set itself again, and a press afterwards add no second Email.
        var lapsingTo = Guest();
        var lapsing = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(19, 0), phone: "0933444555", email: lapsingTo);
        await RunJobsAsync();
        var expiresAt = new DateTimeOffset(2026, 11, 12, 18, 0, 5, TimeSpan.FromHours(8));
        SetNow(expiresAt);
        await RunJobsAsync();
        (await ReservationOfAsync(shop, lapsing.Id)).Status.ShouldBe(CoordinatorReservationStatus.Expired);
        await using (var scope = _host.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<CoordinatorReservations>();
            var now = _host.Clock.GetUtcNow();
            (await store.ExpireAsync(shop.Id, lapsing.Id, version: 1, now, CancellationToken)).ShouldBeNull();
            await store.ScheduleExpiryAsync(shop.Id, lapsing.Id, now, now, CancellationToken);
        }

        await RunJobsAsync();
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, lapsing.Id)))
            .ShouldBe("這筆訂位已過期：訂位時間前 60 分鐘還沒有人處理。");
        await RunJobsAsync();
        (await EmailJobsAsync(shop)).Count(job => Payload(job).ReservationId == lapsing.Id).ShouldBe(1);
        Smtp.To(lapsingTo).ShouldHaveSingleItem().Subject.ShouldBe("一封麵店：訂位未能確認");
        (await ReservationJobsAsync(shop))
            .Where(job => job.Kind == CoordinatorExpireReservationJob.Kind
                && JsonSerializer.Deserialize<CoordinatorExpireReservationJob>(job.Payload, JsonSerializerOptions.Web)!.ReservationId == lapsing.Id)
            .Select(job => job.Status)
            .ShouldBe([BackgroundJobStatus.Succeeded, BackgroundJobStatus.Succeeded], "the expiry job did run again");
    }

    [Fact]
    public async Task Without_an_address_or_without_SMTP_nothing_is_queued_and_the_members_are_told_to_call()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("來電麵店");
        var none = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30), email: null);
        await RunJobsAsync();
        ReservationCard(CardsTo(owner).ShouldHaveSingleItem()).Body
            .ShouldBe(["11/12（四） 18:30", "4 位", "姓名", "陳小姐", "電話", "0912-345-678", "Email", "沒有留 Email，請來電確認", "備註", "需要兒童椅"]);
        var accepted = (await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, none.Id))).ShouldHaveSingleItem();
        Said(accepted).ShouldBe("已接受：陳小姐 11/12（四） 18:30 4 位。這位顧客沒有留 Email，請來電 0912-345-678。");
        Elements(accepted.GetProperty("contents").GetProperty("footer"))
            .Where(element => element.TryGetProperty("uri", out _))
            .Select(element => element.GetProperty("uri").GetString())
            .ShouldBe(["tel:0912345678"], "R2's 〔撥打電話〕 stays");
        (await EmailJobsAsync(shop)).ShouldBeEmpty();

        // Queued while SMTP was set, run after it was taken away: nothing is sent, the job completes.
        var queuedTo = Guest();
        var queued = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(20, 0), phone: "0944555666", email: queuedTo);
        await RunJobsAsync();
        Said((await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, queued.Id)))[0])
            .ShouldEndWith("會寄確認信給顧客。");
        (await EmailJobsAsync(shop)).ShouldHaveSingleItem();

        _host.SmtpConfigured = false;
        try
        {
            await RunJobsAsync();
            (await EmailJobsAsync(shop)).ShouldHaveSingleItem().Status.ShouldBe(BackgroundJobStatus.Succeeded);
            Smtp.To(queuedTo).ShouldBeEmpty();

            var leftTo = Guest();
            var left = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(19, 0), phone: "0911222333", email: leftTo, note: null);
            var lapsing = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(19, 30), phone: "0922333444", email: null);
            await RunJobsAsync();

            var cards = CardsTo(manager).Select(ReservationCard).ToList();
            cards.Single(card => card.AltText.Contains("19:00", StringComparison.Ordinal)).Body
                .ShouldBe(["11/12（四） 19:00", "4 位", "姓名", "陳小姐", "電話", "0911-222-333", "Email", leftTo, "請來電確認"]);
            cards.Single(card => card.AltText.Contains("19:30", StringComparison.Ordinal)).Body
                .ShouldBe(["11/12（四） 19:30", "4 位", "姓名", "陳小姐", "電話", "0922-333-444", "備註", "需要兒童椅", "請來電確認"]);

            Said((await PostbackForMessagesAsync(manager, CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, left.Id))).ShouldHaveSingleItem())
                .ShouldBe("已婉拒：陳小姐 11/12（四） 19:00 4 位。平台沒有設定寄信，請來電 0911-222-333。");
            SetNow(new DateTimeOffset(2026, 11, 12, 18, 30, 5, TimeSpan.FromHours(8)));
            await RunJobsAsync();
            (await ReservationOfAsync(shop, lapsing.Id)).Status.ShouldBe(CoordinatorReservationStatus.Expired);

            (await EmailJobsAsync(shop)).ShouldHaveSingleItem("only the one queued while SMTP was set");
            Smtp.To(leftTo).ShouldBeEmpty();
        }
        finally
        {
            _host.SmtpConfigured = true;
        }
    }

    [Fact]
    public async Task A_refused_Email_is_retried_with_backoff_and_its_error_has_no_address()
    {
        SetNow(ReservationNow);
        var (shop, owner, _, _) = await ReservationShopAsync("重寄麵店");
        var to = Guest();
        var reservation = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 30), email: to);
        await RunJobsAsync();
        Smtp.FailNext(2);
        try
        {
            Said((await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, reservation.Id)))[0])
                .ShouldBe("已接受：陳小姐 11/12（四） 18:30 4 位。會寄確認信給顧客。", "the press does not wait for the Email");
            await RunJobsAsync();

            var job = (await EmailJobsAsync(shop)).ShouldHaveSingleItem();
            (job.Status, job.Attempts).ShouldBe((BackgroundJobStatus.Queued, 1), "a retry is scheduled");
            job.LastError.ShouldNotBeNull().ShouldContain("The reservation Email (accepted) could not be sent (attempt 1 of 5)");
            job.LastError.ShouldContain("SmtpException");
            job.LastError.ShouldNotContain(to);
            Smtp.To(to).ShouldBeEmpty();

            _host.Clock.Advance(TimeSpan.FromSeconds(31));
            await RunJobsAsync();
            (await EmailJobsAsync(shop)).Single().Attempts.ShouldBe(2);
            Smtp.To(to).ShouldBeEmpty();

            _host.Clock.Advance(TimeSpan.FromSeconds(61));
            await RunJobsAsync();
            var sent = (await EmailJobsAsync(shop)).Single();
            (sent.Status, sent.Attempts).ShouldBe((BackgroundJobStatus.Succeeded, 3));
            Smtp.To(to).ShouldHaveSingleItem().Subject.ShouldBe("重寄麵店：訂位已確認");
        }
        finally
        {
            Smtp.FailNext(0);
        }
    }

    // --- Helpers --------------------------------------------------------------------------------------

    /// <summary>A customer's address no other test uses.</summary>
    private static string Guest() => $"guest-{Guid.NewGuid():N}@example.com";

    private static string Footer(string storeName) => string.Format(System.Globalization.CultureInfo.InvariantCulture, EmailFooter, storeName);

    private static CoordinatorReservationEmailJob Payload(BackgroundJob job) =>
        JsonSerializer.Deserialize<CoordinatorReservationEmailJob>(job.Payload, JsonSerializerOptions.Web)!;

    private async Task<List<BackgroundJob>> EmailJobsAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.BackgroundJobs.AsNoTracking()
            .Where(job => job.Kind == CoordinatorReservationEmailJob.Kind)
            .OrderBy(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .ToListAsync(CancellationToken);
    }
}
