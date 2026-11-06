using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Ordering;
using SmartAgri.Api.Organizations;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Ordering;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Ordering;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Notifications;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Ordering;

/// <summary>
/// What follows an online order (online-ordering Slice O4, #400) through the real endpoints, compare-and-sets, job queue
/// (at the host's clock), <c>SmtpEmailSender</c> and a <see cref="FakeSmtpServer"/>: the customer's Email for each answer —
/// one job per change, by the transition that won, none without an address or SMTP, retried when SMTP refuses without
/// touching the order; the expiry counted in ordering hours only and capped at the pickup, racing a member's press; and the
/// 90-day clearing of the customer's personal data, as the staff and the customer then see the order.
/// </summary>
/// <remarks>Each shop takes orders every day 11:00–14:00 and 17:00–21:00 (minimum lead 20 minutes, the example file).</remarks>
[Trait("Category", TestCategories.Docker)]
public sealed class OrderingFollowUpTests : IClassFixture<OrderingFollowUpHostFixture>
{
    private const string Password = "Staff-Password-1!";

    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    /// <summary>Tuesday 2026-11-17 12:00 in Taipei.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 11, 17, 12, 0, 0, Taipei);

    private static int _phones;

    private readonly OrderingFollowUpHostFixture _host;
    private readonly HttpClient _client;

    public OrderingFollowUpTests(OrderingFollowUpHostFixture host)
    {
        _host = host;
        SetNow(Noon);
        _client = host.Factory.CreateClient();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private FakeSmtpServer Smtp => _host.Smtp;

    // --- Emails ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_Emails_the_customer_once_with_the_order_the_shop_and_its_page_but_nothing_they_typed()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var to = Guest();
        var submitted = await SubmitAsync(slug, "2026-11-17T12:30", to);
        var token = submitted["token"]!.GetValue<string>();
        var id = await OrderIdAsync(shop, submitted);
        (await EmailJobsAsync(shop)).ShouldBeEmpty("nothing is sent while it waits");

        var later = new DateTimeOffset(2026, 11, 17, 12, 45, 0, Taipei);
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/accept", staff, new { version = 1, promisedAt = later }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var job = (await EmailJobsAsync(shop)).ShouldHaveSingleItem();
        (Payload(job).OrderId, Payload(job).Outcome, job.Status).ShouldBe((id, OrderingOrderStatus.Accepted, BackgroundJobStatus.Queued));
        Smtp.To(to).ShouldBeEmpty("the press is answered first; the Email is the job queue's");

        await RunJobsAsync();

        var email = Smtp.To(to).ShouldHaveSingleItem();
        email.Recipients.ShouldBe([to]);
        email.FromHeader.ShouldBe($"王記麵店 <{OrderingFollowUpHostFixture.SmtpFrom}>", "the shop's name at the platform's address");
        email.Subject.ShouldBe("王記麵店：已接單（訂單 #001）");
        email.Body.ShouldBe(
            "王記麵店已接受您的訂單 #001，預計 11/17（二） 12:45 可取餐（店家調整了取餐時間）。請準時到店取餐，取餐時付款。如需更改或取消，請來電 06-123-4567。\n\n"
            + "訂單編號：#001\n店家：王記麵店\n取餐：外帶，11/17（二） 12:45\n付款：取餐時付款\n店家電話：06-123-4567\n\n"
            + "明細：\n・紅燒牛肉麵 ×1　NT$270\n　大碗、寬麵、滷蛋 ×2、小菜飲料套餐（涼拌小黃瓜、冬瓜檸檬）\n合計：NT$270\n\n"
            + $"王記麵店的點餐網頁：{OrderingFollowUpHostFixture.PublicBaseUrl}/order/{slug}\n\n"
            + "——\n這封信由線上點餐系統代王記麵店寄出；回覆這封信不會有人讀取。\n");
        foreach (var secret in new[] { token, "陳小姐", Phone(_phones), "麵硬一點", "請放門口", to })
        {
            email.Body.ShouldNotContain(secret);
        }

        (await EmailJobsAsync(shop)).Single().Status.ShouldBe(BackgroundJobStatus.Succeeded);
        await RunJobsAsync();
        Smtp.To(to).Count.ShouldBe(1, "one Email per change");
    }

    [Fact]
    public async Task Declining_cancelling_by_the_shop_and_by_the_customer_each_Email_why()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var (declinedTo, cancelledTo, withdrawnTo) = (Guest(), Guest(), Guest());
        var declined = await OrderIdAsync(shop, await SubmitAsync(slug, "2026-11-17T12:30", declinedTo));
        var cancelled = await OrderIdAsync(shop, await SubmitAsync(slug, "2026-11-17T13:00", cancelledTo));
        var withdrawn = await SubmitAsync(slug, "2026-11-17T13:30", withdrawnTo);

        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{declined}/decline", staff, new { version = 1, reason = "sold-out", note = "牛肉麵賣完了" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{cancelled}/accept", staff, new { version = 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{cancelled}/cancel", staff, new { version = 2, reason = "other", note = "瓦斯沒了" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendWithTokenAsync(HttpMethod.Post, $"/api/v1/public/ordering/{slug}/orders/current/cancel", withdrawn["token"]!.GetValue<string>()))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EmailJobsAsync(shop)).Select(job => Payload(job).Outcome).ShouldBe(
            [OrderingOrderStatus.Declined, OrderingOrderStatus.Accepted, OrderingOrderStatus.Cancelled, OrderingOrderStatus.Cancelled], ignoreOrder: true);

        await RunJobsAsync();

        var regret = Smtp.To(declinedTo).ShouldHaveSingleItem();
        regret.Subject.ShouldBe("王記麵店：無法接受您的訂單（訂單 #001）");
        regret.Body.ShouldStartWith(
            "很抱歉，王記麵店無法接受您的訂單 #001。原因：有品項已經賣完。牛肉麵賣完了。這筆訂單不會製作，也不需要付款。歡迎改天再點，有問題請來電 06-123-4567。\n\n");
        regret.Body.ShouldContain("取餐：外帶，11/17（二） 12:30\n");

        // Accepted, then cancelled before its Email went out: only the cancellation is told.
        var cancellation = Smtp.To(cancelledTo).ShouldHaveSingleItem();
        cancellation.Subject.ShouldBe("王記麵店：店家已取消您的訂單（訂單 #002）");
        cancellation.Body.ShouldStartWith("很抱歉，王記麵店已取消您的訂單 #002。原因：其他原因。瓦斯沒了。這筆訂單不會製作，也不需要付款。有問題請來電 06-123-4567。\n\n");

        var confirmation = Smtp.To(withdrawnTo).ShouldHaveSingleItem();
        confirmation.Subject.ShouldBe("王記麵店：您的訂單已取消（訂單 #003）");
        confirmation.Body.ShouldStartWith("您已取消在王記麵店的訂單 #003，這筆訂單不會製作，也不需要付款。\n\n");
        (await EmailJobsAsync(shop)).ShouldAllBe(job => job.Status == BackgroundJobStatus.Succeeded);
    }

    [Fact]
    public async Task Without_an_address_or_without_SMTP_nothing_is_enqueued_and_the_order_is_answered_as_usual()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var anonymous = await OrderIdAsync(shop, await SubmitAsync(slug, "2026-11-17T12:30", email: null));
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{anonymous}/accept", staff, new { version = 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var withAddress = await OrderIdAsync(shop, await SubmitAsync(slug, "2026-11-17T13:00", Guest()));
        _host.SmtpConfigured = false;
        try
        {
            (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{withAddress}/decline", staff, new { version = 1, reason = "too-busy" }))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            _host.SmtpConfigured = true;
        }

        (await EmailJobsAsync(shop)).ShouldBeEmpty();
        (await StatusOfAsync(shop, anonymous)).ShouldBe(OrderingOrderStatus.Accepted);
        (await StatusOfAsync(shop, withAddress)).ShouldBe(OrderingOrderStatus.Declined);
    }

    [Fact]
    public async Task A_refused_Email_is_retried_and_never_changes_the_order()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var to = Guest();
        var id = await OrderIdAsync(shop, await SubmitAsync(slug, "2026-11-17T12:30", to));
        Smtp.FailNext(1);
        try
        {
            (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/accept", staff, new { version = 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);
            await RunJobsAsync();

            var job = (await EmailJobsAsync(shop)).ShouldHaveSingleItem();
            (job.Status, job.Attempts).ShouldBe((BackgroundJobStatus.Queued, 1), "a retry is scheduled");
            job.LastError.ShouldNotBeNull().ShouldContain("The order Email (accepted) could not be sent (attempt 1 of 5)");
            job.LastError.ShouldNotContain(to);
            (await StatusOfAsync(shop, id)).ShouldBe(OrderingOrderStatus.Accepted);

            _host.Clock.Advance(TimeSpan.FromSeconds(31));
            await RunJobsAsync();
            (await EmailJobsAsync(shop)).Single().Status.ShouldBe(BackgroundJobStatus.Succeeded);
            Smtp.To(to).ShouldHaveSingleItem().Subject.ShouldBe("王記麵店：已接單（訂單 #001）");
            (await StatusOfAsync(shop, id)).ShouldBe(OrderingOrderStatus.Accepted);
        }
        finally
        {
            Smtp.FailNext(0);
        }
    }

    [Fact]
    public async Task Of_many_presses_at_once_one_wins_and_enqueues_one_Email_and_a_lost_press_none()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var id = await OrderIdAsync(shop, await SubmitAsync(slug, "2026-11-17T12:30", Guest()));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            await using var scope = _host.Factory.Services.CreateAsyncScope();
            var orders = scope.ServiceProvider.GetRequiredService<OrderingStaffOrders>();
            var action = index % 2 == 0
                ? new OrderingStaffAction(OrderingOrderAction.Accept, 1)
                : new OrderingStaffAction(OrderingOrderAction.Decline, 1, DeclineReason: OrderingDeclineReason.TooBusy);
            return await orders.ActAsync(shop.Id, id, action, staff.AccountId, _host.Clock.GetUtcNow(), CancellationToken);
        }));

        results.Count(result => result.Outcome == OrderingActOutcome.Done).ShouldBe(1);
        results.Count(result => result.Outcome == OrderingActOutcome.Changed).ShouldBe(7);
        var winner = results.Single(result => result.Outcome == OrderingActOutcome.Done).Order!.Status;
        var job = (await EmailJobsAsync(shop)).ShouldHaveSingleItem("one job, by the transition that won");
        Payload(job).Outcome.ShouldBe(winner);

        // A press on the old version afterwards changes nothing and adds nothing.
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/decline", staff, new { version = 1, reason = "too-busy" }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await EmailJobsAsync(shop)).Count.ShouldBe(1);
    }

    // --- Expiry ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_order_sent_during_ordering_hours_expires_after_the_timeout_and_its_customer_is_told()
    {
        var (shop, slug) = await CreateShopAsync();
        var to = Guest();
        var submitted = await SubmitAsync(slug, "2026-11-17T13:00", to);
        var id = await OrderIdAsync(shop, submitted);
        var order = await OrderAsync(shop, id);
        order.ExpiresAt.ShouldBe(order.CreatedAt.AddMinutes(15));
        var expiry = (await JobsAsync(shop, OrderingExpireOrderJob.Kind)).ShouldHaveSingleItem("enqueued with the order");
        expiry.RunAfter.ShouldBe(order.ExpiresAt);

        SetNow(Noon.AddMinutes(14));
        await RunJobsAsync();
        (await StatusOfAsync(shop, id)).ShouldBe(OrderingOrderStatus.Pending);

        SetNow(order.ExpiresAt.AddSeconds(5));
        await RunJobsAsync();

        (await StatusOfAsync(shop, id)).ShouldBe(OrderingOrderStatus.Expired);
        var email = Smtp.To(to).ShouldHaveSingleItem();
        email.Subject.ShouldBe("王記麵店：訂單已自動取消（訂單 #001）");
        email.Body.ShouldStartWith(
            "很抱歉，店家未能及時確認，這筆訂單已自動取消（王記麵店，訂單 #001）。這筆訂單不會製作，也不需要付款。需要的話請重新點餐，或請來電 06-123-4567。\n\n");
        var view = await JsonAsync(await SendWithTokenAsync(HttpMethod.Get, $"/api/v1/public/ordering/{slug}/orders/current", submitted["token"]!.GetValue<string>()));
        (view["status"]!.GetValue<string>(), view["canCancel"]!.GetValue<bool>()).ShouldBe(("expired", false));

        // The job run again (a retried delivery) changes and sends nothing more.
        await using (var scope = _host.Factory.Services.CreateAsyncScope())
        {
            var again = await scope.ServiceProvider.GetRequiredService<OrderingOrders>().ExpireAsync(shop.Id, id, _host.Clock.GetUtcNow(), CancellationToken);
            again.Outcome.ShouldBe(OrderingExpireOutcome.Answered);
        }

        (await JobsAsync(shop, OrderingOrderEmailJob.Kind)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_order_sent_at_night_waits_until_opening_and_expires_after_the_timeout_or_at_its_pickup()
    {
        var (shop, slug) = await CreateShopAsync();
        SetNow(new DateTimeOffset(2026, 11, 17, 22, 0, 0, Taipei));
        var (laterTo, firstTo) = (Guest(), Guest());
        var later = await OrderIdAsync(shop, await SubmitAsync(slug, "2026-11-18T11:30", laterTo));
        var first = await OrderIdAsync(shop, await SubmitAsync(slug, "2026-11-18T11:00", firstTo));
        (await OrderAsync(shop, later)).ExpiresAt.ShouldBe(new DateTimeOffset(2026, 11, 18, 11, 15, 0, Taipei), "15 minutes after the hours open");
        (await OrderAsync(shop, first)).ExpiresAt.ShouldBe(new DateTimeOffset(2026, 11, 18, 11, 0, 0, Taipei), "at the latest its pickup");

        SetNow(new DateTimeOffset(2026, 11, 18, 8, 0, 0, Taipei));
        await RunJobsAsync();
        (await StatusOfAsync(shop, later)).ShouldBe(OrderingOrderStatus.Pending, "not declined overnight");
        (await StatusOfAsync(shop, first)).ShouldBe(OrderingOrderStatus.Pending);

        SetNow(new DateTimeOffset(2026, 11, 18, 11, 0, 5, Taipei));
        await RunJobsAsync();
        (await StatusOfAsync(shop, first)).ShouldBe(OrderingOrderStatus.Expired);
        (await StatusOfAsync(shop, later)).ShouldBe(OrderingOrderStatus.Pending);

        SetNow(new DateTimeOffset(2026, 11, 18, 11, 15, 5, Taipei));
        await RunJobsAsync();
        (await StatusOfAsync(shop, later)).ShouldBe(OrderingOrderStatus.Expired);
        Smtp.To(laterTo).ShouldHaveSingleItem().Subject.ShouldBe("王記麵店：訂單已自動取消（訂單 #001）");
        Smtp.To(firstTo).ShouldHaveSingleItem().Subject.ShouldBe("王記麵店：訂單已自動取消（訂單 #002）");
    }

    [Fact]
    public async Task An_asap_order_expires_after_the_shops_timeout_capped_at_its_estimate()
    {
        var (shop, slug) = await CreateShopAsync(timeoutMinutes: 5);
        var quick = await OrderIdAsync(shop, await SubmitAsync(slug, slot: null, Guest()));
        var quickOrder = await OrderAsync(shop, quick);
        quickOrder.ExpiresAt.ShouldBe(quickOrder.CreatedAt.AddMinutes(5));

        var (slow, slowSlug) = await CreateShopAsync(timeoutMinutes: 120);
        var capped = await OrderAsync(slow, await OrderIdAsync(slow, await SubmitAsync(slowSlug, slot: null, Guest())));
        capped.ExpiresAt.ShouldBe(capped.PickupAt, "the estimate's end (30 minutes) comes before 120 minutes");

        SetNow(quickOrder.ExpiresAt.AddSeconds(5));
        await RunJobsAsync();
        (await StatusOfAsync(shop, quick)).ShouldBe(OrderingOrderStatus.Expired);
        (await StatusOfAsync(slow, capped.Id)).ShouldBe(OrderingOrderStatus.Pending);
    }

    [Fact]
    public async Task An_expiry_and_a_press_at_the_same_moment_exactly_one_wins()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var ids = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            ids.Add(await OrderIdAsync(shop, await SubmitAsync(slug, "2026-11-17T13:00", Guest())));
        }

        SetNow(Noon.AddMinutes(16));
        foreach (var id in ids)
        {
            var now = _host.Clock.GetUtcNow();
            var expire = Task.Run(async () =>
            {
                await using var scope = _host.Factory.Services.CreateAsyncScope();
                return (await scope.ServiceProvider.GetRequiredService<OrderingOrders>().ExpireAsync(shop.Id, id, now, CancellationToken)).Outcome;
            });
            var accept = Task.Run(async () =>
            {
                await using var scope = _host.Factory.Services.CreateAsyncScope();
                return (await scope.ServiceProvider.GetRequiredService<OrderingStaffOrders>()
                    .ActAsync(shop.Id, id, new OrderingStaffAction(OrderingOrderAction.Accept, 1), staff.AccountId, now, CancellationToken)).Outcome;
            });
            var (expired, accepted) = (await expire, await accept);

            (expired == OrderingExpireOutcome.Expired).ShouldNotBe(accepted == OrderingActOutcome.Done, "exactly one of the two");
            var status = await StatusOfAsync(shop, id);
            status.ShouldBe(expired == OrderingExpireOutcome.Expired ? OrderingOrderStatus.Expired : OrderingOrderStatus.Accepted);
            (await JobsAsync(shop, OrderingOrderEmailJob.Kind)).Count(job => Payload(job).OrderId == id).ShouldBe(1);
        }

        // The queue's own expiry jobs then find every order answered: nothing changes, no Email is added.
        await RunJobsAsync();
        (await JobsAsync(shop, OrderingOrderEmailJob.Kind)).Count.ShouldBe(4);
    }

    // --- Retention -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ninety_days_after_the_pickup_the_customers_details_are_cleared_and_both_views_still_show_the_order()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var submitted = await SubmitAsync(slug, "2026-11-17T12:30", Guest(), comment: "請放門口");
        var token = submitted["token"]!.GetValue<string>();
        var id = await OrderIdAsync(shop, submitted);
        var promised = new DateTimeOffset(2026, 11, 17, 13, 0, 0, Taipei);
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/accept", staff, new { version = 1, promisedAt = promised }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var (other, otherSlug) = await CreateShopAsync();
        var neighbour = await OrderIdAsync(other, await SubmitAsync(otherSlug, "2026-11-17T12:30", Guest()));

        // Counted from the promised 13:00, not the chosen 12:30.
        (await ClearAsync(shop, promised.AddDays(90).AddMinutes(-1))).ShouldBe(0, "89 days 23:59 after the pickup: kept");
        (await ClearAsync(shop, promised.AddDays(90))).ShouldBe(1, "90 days after the pickup: cleared");
        (await ClearAsync(shop, promised.AddDays(91))).ShouldBe(0, "already cleared");

        var cleared = await OrderAsync(shop, id);
        (cleared.CustomerName, cleared.CustomerPhone, cleared.CustomerEmail, cleared.OrderComment).ShouldBe((string.Empty, string.Empty, null, null));
        cleared.Lines.ShouldAllBe(line => line.Comment == null);
        (cleared.Number, cleared.Status, cleared.Total, cleared.Version).ShouldBe((1, OrderingOrderStatus.Accepted, 270, 2));
        cleared.Lines.Single().Options.Count.ShouldBe(4, "the items and options stay for the sales figures");
        cleared.PersonalDataClearedAt.ShouldNotBeNull();
        (await OrderAsync(other, neighbour)).CustomerName.ShouldBe("陳小姐", "another organization is untouched");

        var staffView = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/ordering/orders/{id}", staff));
        (staffView["customerName"]!.GetValue<string>(), staffView["customerPhone"]!.GetValue<string>(), staffView["customerPhoneDisplay"]!.GetValue<string>())
            .ShouldBe(("（已刪除）", string.Empty, string.Empty));
        (staffView["customerEmail"], staffView["orderComment"], staffView["lines"]![0]!["comment"]).ShouldBe((null, null, null));
        staffView["personalDataClearedAt"].ShouldNotBeNull();
        staffView["total"]!.GetValue<int>().ShouldBe(270);

        var publicView = await JsonAsync(await SendWithTokenAsync(HttpMethod.Get, $"/api/v1/public/ordering/{slug}/orders/current", token));
        (publicView["customerName"]!.GetValue<string>(), publicView["phoneLastDigits"]!.GetValue<string>(), publicView["orderComment"])
            .ShouldBe(("（已刪除）", string.Empty, null));
        publicView["displayNumber"]!.GetValue<string>().ShouldBe("#001");
    }

    [Fact]
    public async Task The_daily_sweep_and_the_subcommand_clear_old_orders_of_every_organization()
    {
        var (shop, slug) = await CreateShopAsync();
        var old = await OrderIdAsync(shop, await SubmitAsync(slug, "2026-11-17T12:30", Guest()));
        var (other, otherSlug) = await CreateShopAsync();
        var otherOld = await OrderIdAsync(other, await SubmitAsync(otherSlug, "2026-11-17T12:30", Guest()));
        SetNow(Noon.AddDays(60));
        var recent = await OrderIdAsync(shop, await SubmitAsync(slug, "2027-01-16T12:30", Guest()));

        var asOf = new DateTimeOffset(2027, 2, 16, 3, 0, 0, Taipei);
        (await _host.Factory.Services.GetRequiredService<HandoffRetentionSweep>().SweepAsync(asOf, CancellationToken)).ShouldBeGreaterThanOrEqualTo(2);
        (await OrderAsync(shop, old)).PersonalDataClearedAt.ShouldNotBeNull();
        (await OrderAsync(other, otherOld)).PersonalDataClearedAt.ShouldNotBeNull();
        (await OrderAsync(shop, recent)).PersonalDataClearedAt.ShouldBeNull();

        SetNow(Noon);
        var (third, thirdSlug) = await CreateShopAsync();
        var thirdOld = await OrderIdAsync(third, await SubmitAsync(thirdSlug, "2026-11-17T12:30", Guest()));
        var (fourth, fourthSlug) = await CreateShopAsync();
        var fourthOld = await OrderIdAsync(fourth, await SubmitAsync(fourthSlug, "2026-11-17T12:30", Guest()));
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await RetentionCleanupCommand.RunAsync(
            _host.Factory.Services,
            ["--organization", third.Code, "--as-of", asOf.ToString("o", System.Globalization.CultureInfo.InvariantCulture)],
            output,
            error,
            CancellationToken);

        exit.ShouldBe(RetentionCleanupCommand.ExitSuccess, error.ToString());
        output.ToString().ShouldContain("線上點餐：清除 1 筆");
        (await OrderAsync(third, thirdOld)).PersonalDataClearedAt.ShouldNotBeNull();
        (await OrderAsync(fourth, fourthOld)).PersonalDataClearedAt.ShouldBeNull("only the named organization");
    }

    // --- Helpers ---------------------------------------------------------------------------------------------

    private sealed record Staff(string Token, Guid AccountId);

    private void SetNow(DateTimeOffset at) => _host.Clock.Advance(at - _host.Clock.GetUtcNow());

    private static string Guest() => $"guest-{Guid.NewGuid():N}@example.com";

    private static string Phone(int index) => "0922" + index.ToString("000000", System.Globalization.CultureInfo.InvariantCulture);

    private Task RunJobsAsync() => _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    private async Task<int> ClearAsync(Organization shop, DateTimeOffset asOf)
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
        await using var dbContext = new AppDbContext(options, new FixedOrganizationContext(shop.Id));
        var cleanup = ActivatorUtilities.CreateInstance<RetentionCleanupService>(scope.ServiceProvider, dbContext);
        return (await cleanup.ClearOrderPersonalDataAsync(asOf, CancellationToken)).Count;
    }

    private async Task<(Organization Shop, string Slug)> CreateShopAsync(int? timeoutMinutes = null)
    {
        var shop = await _host.CreateOrganizationAsync("王記麵店");
        var slug = "shop-" + Guid.NewGuid().ToString("N")[..10];
        var tree = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "deploy", "ordering-shop.example.json")))!.AsObject();
        tree["shop"]!["slug"] = slug;
        tree["shop"]!["maxOrdersPerSlot"] = null;
        if (timeoutMinutes is { } minutes)
        {
            tree["shop"]!["pendingTimeoutMinutes"] = minutes;
        }

        var hours = new JsonObject();
        foreach (var day in new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" })
        {
            hours[day] = JsonNode.Parse("""[{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }]""");
        }

        tree["shop"]!["orderingHours"] = hours;
        var parsed = OrderingShopJson.Parse(tree.ToJsonString());
        parsed.Errors.ShouldBeEmpty();
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<OrderingShops>().ImportAsync(shop.Id, parsed.Import!, CancellationToken)).Outcome
            .ShouldBe(OrderingImportOutcome.Created);
        return (shop, slug);
    }

    private async Task<Staff> SignInAsync(Organization organization, string loginName)
    {
        var account = await _host.CreateAccountAsync(organization, loginName, Password, AccountRole.InternalEmployee, null, AccountPermission.ManageOrders);
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, loginName, Password);
        return new Staff(token.AccessToken, account.Id);
    }

    /// <summary>One large beef noodle with options (and the comment 「麵硬一點」) for the scheduled <paramref name="slot"/>
    /// (ASAP when <see langword="null"/>), from a new phone number, with <paramref name="email"/>.</summary>
    private async Task<JsonNode> SubmitAsync(string slug, string? slot, string? email, string? comment = null)
    {
        var phone = Phone(Interlocked.Increment(ref _phones));
        var line = JsonNode.Parse("""
            {"itemId":"beef-noodle","quantity":1,"comment":"麵硬一點","selections":[
              {"groupId":"size","optionId":"large","count":1,"selections":null},
              {"groupId":"noodle","optionId":"wide","count":1,"selections":null},
              {"groupId":"toppings","optionId":"egg","count":2,"selections":null},
              {"groupId":"combo","optionId":"set","count":1,"selections":[
                {"groupId":"side","optionId":"cucumber","count":1,"selections":null},
                {"groupId":"drink","optionId":"winter-melon","count":1,"selections":null}]}]}
            """)!;
        var body = new JsonObject
        {
            ["serviceType"] = slot is null ? "takeout-asap" : "takeout-scheduled",
            ["pickupDate"] = slot?[..10],
            ["pickupSlot"] = slot,
            ["menuVersion"] = 1,
            ["lines"] = new JsonArray(line),
            ["customer"] = new JsonObject { ["name"] = "陳小姐", ["phone"] = phone, ["email"] = email },
            ["orderComment"] = comment,
            ["tableware"] = null,
            ["submissionKey"] = Guid.NewGuid().ToString("D"),
        };
        var response = await _client.PostAsync(
            $"/api/v1/public/ordering/{slug}/orders",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            CancellationToken);
        return await JsonAsync(response, HttpStatusCode.Created);
    }

    private async Task<Guid> OrderIdAsync(Organization shop, JsonNode submitted)
    {
        var hash = OrderingOrder.HashStatusToken(submitted["token"]!.GetValue<string>());
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.OrderingOrders.Where(order => order.StatusTokenHash == hash).Select(order => order.Id).SingleAsync(CancellationToken);
    }

    private async Task<OrderingOrder> OrderAsync(Organization shop, Guid id)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.OrderingOrders.AsNoTracking().SingleAsync(order => order.Id == id, CancellationToken);
    }

    private async Task<OrderingOrderStatus> StatusOfAsync(Organization shop, Guid id) => (await OrderAsync(shop, id)).Status;

    private async Task<List<BackgroundJob>> JobsAsync(Organization shop, string kind)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.BackgroundJobs.AsNoTracking().Where(job => job.Kind == kind).OrderBy(job => job.CreatedAt).ToListAsync(CancellationToken);
    }

    private Task<List<BackgroundJob>> EmailJobsAsync(Organization shop) => JobsAsync(shop, OrderingOrderEmailJob.Kind);

    private static OrderingOrderEmailJob Payload(BackgroundJob job) => JsonSerializer.Deserialize<OrderingOrderEmailJob>(job.Payload, JsonSerializerOptions.Web)!;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Staff staff, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staff.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(OrderingPublicEndpoints.TokenHeader, token);
        return await _client.SendAsync(request, CancellationToken);
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(expected, text);
        return JsonNode.Parse(text)!;
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

/// <summary>A host whose Emails go to a <see cref="FakeSmtpServer"/>, with the real <see cref="IOrderingOrderOutcomes"/> and
/// a public address for the ordering page's link.</summary>
public sealed class OrderingFollowUpHostFixture : AuthHostFixture
{
    public const string SmtpFrom = "order@example.org";

    public const string PublicBaseUrl = "https://order.example.org";

    public FakeSmtpServer Smtp { get; } = new();

    /// <summary>Whether the platform's SMTP server is set (a test may clear it, and must set it back).</summary>
    public bool SmtpConfigured
    {
        get => SmtpOptions.IsConfigured;
        set => SmtpOptions.Host = value ? "127.0.0.1" : null;
    }

    private SmtpOptions SmtpOptions => Factory.Services.GetRequiredService<IOptions<SmtpOptions>>().Value;

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.Configure<SmtpOptions>(options =>
        {
            options.Host = "127.0.0.1";
            options.Port = Smtp.Port;
            options.From = SmtpFrom;
            options.UseStartTls = false;
        });
        services.Configure<PublicChannelsOptions>(options => options.PublicBaseUrl = PublicBaseUrl);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Smtp.DisposeAsync();
    }
}
