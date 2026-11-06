using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ordering;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Ordering;

/// <summary>
/// The admin's online-ordering API (online-ordering design D3, D5, D7, D9, Slice O3, #399) under <see cref="Prefix"/>: the
/// shop's orders and the staff's answers to them, and the shop's switches. The full contract is §4.4 of
/// <c>docs/plans/2026-11-06-online-ordering.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Signed in, with <c>manage-orders</c>, for the caller's own organization (the token's). Without the permission, and for
/// an order id the organization does not have, the answer is the same <c>403 ordering</c> (<see cref="ForbiddenReason.Ordering"/>).
/// An organization without ordering settings or menu gets <c>404 ordering-not-configured</c> from every endpoint.
/// </para>
/// <para>
/// Logs carry the organization, the order id and number, the member and the action — never the customer's name, phone,
/// Email or comments (they are in the responses only).
/// </para>
/// </remarks>
public static class OrderingStaffEndpoints
{
    public const string Prefix = "/api/v1/ordering";

    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapOrderingStaffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var ordering = endpoints.MapGroup(Prefix)
            .RequireAuthorization()
            .RequirePermission(AccountPermission.ManageOrders, ForbiddenReason.Ordering)
            .WithTags("Ordering (admin)")
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            });

        ordering.MapGet("/summary", SummaryAsync)
            .WithSummary("Counts and cursors to poll: pending orders, the newest order, the last change, the shop's switches")
            .Produces<OrderingStaffSummaryView>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        ordering.MapGet("/orders", ListAsync)
            .WithSummary("The orders of one view: pending, active (accepted and ready) or done (today's closed orders)")
            .WithDescription("422 when view is not pending, active or done.")
            .Produces<OrderingStaffOrdersView>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        ordering.MapGet("/orders/{orderId:guid}", GetAsync)
            .WithSummary("One order")
            .Produces<OrderingStaffOrderView>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        MapAction<OrderingAcceptRequest>(ordering, "accept", "Accept a pending order, optionally for a later time", AcceptAsync);
        MapAction<OrderingDeclineRequest>(ordering, "decline", "Decline a pending order with a reason", DeclineAsync);
        MapAction<OrderingStepRequest>(ordering, "ready", "Mark an accepted order ready to pick up", ReadyAsync);
        MapAction<OrderingStepRequest>(ordering, "complete", "Mark an accepted or ready order picked up", CompleteAsync);
        MapAction<OrderingShopCancelRequest>(ordering, "cancel", "Cancel an accepted or ready order with a reason", CancelAsync);

        ordering.MapGet("/tables", TablesAsync)
            .WithSummary("The open table sessions (dine-in): each table's orders until 〔已結帳〕")
            .Produces<OrderingStaffTablesView>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        ordering.MapPost("/tables/sessions/{sessionId:guid}/close", CloseSessionAsync)
            .WithSummary("〔已結帳〕: close a table session and complete its accepted and ready orders")
            .WithDescription(
                "200 with the closed session. 409 session-changed (another order joined, or it was closed, since the version sent) or " +
                "table-has-pending (an order is still pending), both with the session as it is now. 403 ordering for a session the " +
                "organization does not have. 422 for a missing version.")
            .Accepts<OrderingCloseSessionRequest>("application/json")
            .Produces<OrderingStaffTableSessionView>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<OrderingStaffTableConflict>(StatusCodes.Status409Conflict, ProblemJson)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        ordering.MapPut("/pause", PauseAsync)
            .WithSummary("Pause or resume taking orders (暫停接單)")
            .Produces<OrderingStaffSummaryView>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        ordering.MapPut("/asap-band", AsapBandAsync)
            .WithSummary("Set how long an ASAP order takes now (即時單準備時間)")
            .Produces<OrderingStaffSummaryView>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        ordering.MapGet("/items", ItemsAsync)
            .WithSummary("The current menu's items and whether each can be ordered")
            .Produces<OrderingStaffMenuView>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        ordering.MapPut("/items/{itemId}/state", ItemStateAsync)
            .WithSummary("Set an item available, sold out today (今日售完) or unavailable (停售)")
            .WithDescription("404 item-not-found when the current menu has no such item.")
            .Produces<OrderingStaffItemView>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static void MapAction<TRequest>(
        RouteGroupBuilder ordering,
        string action,
        string summary,
        Func<Guid, TRequest, HttpContext, IOrganizationContext, OrderingStaffOrders, TimeProvider, ILogger<OrderingStaffOrders>, CancellationToken, Task<IResult>> handler)
        where TRequest : notnull
    {
        ordering.MapPost($"/orders/{{orderId:guid}}/{action}", handler)
            .WithName("OrderingOrder" + char.ToUpperInvariant(action[0]) + action[1..])
            .WithSummary(summary)
            .WithDescription(
                "200 with the order as it is now. 409 order-changed (the order is not at the version sent) or action-not-allowed " +
                "(its status does not allow this), both with the order as it is now. 422 for a missing version or reason, a " +
                "note too long or missing with other, or a promised time out of range.")
            .Accepts<TRequest>("application/json")
            .Produces<OrderingStaffOrderView>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<OrderingStaffConflict>(StatusCodes.Status409Conflict, ProblemJson)
            .Produces(StatusCodes.Status422UnprocessableEntity);
    }

    internal static async Task<IResult> SummaryAsync(
        IOrganizationContext organization, OrderingStaffOrders orders, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId)
        {
            return ApiErrors.Unauthorized();
        }

        var now = clock.GetUtcNow();
        return await orders.SummaryAsync(organizationId, now, cancellationToken) is { } summary
            ? Results.Ok(OrderingStaffSummaryView.Of(summary, now))
            : NotConfigured();
    }

    internal static async Task<IResult> ListAsync(
        string? view, IOrganizationContext organization, OrderingStaffOrders orders, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await orders.ShopAsync(organizationId, cancellationToken) is not var (shop, _))
        {
            return NotConfigured();
        }

        OrderingOrderList? list = view switch
        {
            "pending" => OrderingOrderList.Pending,
            "active" => OrderingOrderList.Active,
            "done" => OrderingOrderList.Done,
            _ => null,
        };
        if (list is null)
        {
            return Invalid("view", "view 要是 pending、active 或 done。");
        }

        var now = clock.GetUtcNow();
        var (listed, members) = await orders.ListAsync(organizationId, shop, list.Value, now, cancellationToken);
        return Results.Ok(new OrderingStaffOrdersView(now, view!, [.. listed.Select(order => OrderingStaffOrderView.Of(order, members))]));
    }

    internal static async Task<IResult> GetAsync(
        Guid orderId, IOrganizationContext organization, OrderingStaffOrders orders, CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await orders.ShopAsync(organizationId, cancellationToken) is null)
        {
            return NotConfigured();
        }

        if (await orders.FindAsync(organizationId, orderId, cancellationToken) is not { } order)
        {
            return ApiErrors.NotFound(ForbiddenReason.Ordering);
        }

        return Results.Ok(OrderingStaffOrderView.Of(order, await orders.MembersAsync(organizationId, order, cancellationToken)));
    }

    internal static Task<IResult> AcceptAsync(
        Guid orderId, OrderingAcceptRequest request, HttpContext httpContext, IOrganizationContext organization, OrderingStaffOrders orders,
        TimeProvider clock, ILogger<OrderingStaffOrders> logger, CancellationToken cancellationToken) =>
        request.Version is not { } version
            ? Task.FromResult(MissingVersion())
            : ActAsync(
                orderId, new OrderingStaffAction(OrderingOrderAction.Accept, version, PromisedAt: request.PromisedAt), httpContext, organization,
                orders, clock, logger, cancellationToken);

    internal static Task<IResult> DeclineAsync(
        Guid orderId, OrderingDeclineRequest request, HttpContext httpContext, IOrganizationContext organization, OrderingStaffOrders orders,
        TimeProvider clock, ILogger<OrderingStaffOrders> logger, CancellationToken cancellationToken)
    {
        var note = Trimmed(request.Note);
        if (ReasonErrors(request.Version, request.Reason is null, note, request.Reason == OrderingDeclineReason.Other) is { } invalid)
        {
            return Task.FromResult(invalid);
        }

        return ActAsync(
            orderId, new OrderingStaffAction(OrderingOrderAction.Decline, request.Version!.Value, DeclineReason: request.Reason, Note: note),
            httpContext, organization, orders, clock, logger, cancellationToken);
    }

    internal static Task<IResult> ReadyAsync(
        Guid orderId, OrderingStepRequest request, HttpContext httpContext, IOrganizationContext organization, OrderingStaffOrders orders,
        TimeProvider clock, ILogger<OrderingStaffOrders> logger, CancellationToken cancellationToken) =>
        request.Version is not { } version
            ? Task.FromResult(MissingVersion())
            : ActAsync(orderId, new OrderingStaffAction(OrderingOrderAction.MarkReady, version), httpContext, organization, orders, clock, logger, cancellationToken);

    internal static Task<IResult> CompleteAsync(
        Guid orderId, OrderingStepRequest request, HttpContext httpContext, IOrganizationContext organization, OrderingStaffOrders orders,
        TimeProvider clock, ILogger<OrderingStaffOrders> logger, CancellationToken cancellationToken) =>
        request.Version is not { } version
            ? Task.FromResult(MissingVersion())
            : ActAsync(orderId, new OrderingStaffAction(OrderingOrderAction.Complete, version), httpContext, organization, orders, clock, logger, cancellationToken);

    internal static Task<IResult> CancelAsync(
        Guid orderId, OrderingShopCancelRequest request, HttpContext httpContext, IOrganizationContext organization, OrderingStaffOrders orders,
        TimeProvider clock, ILogger<OrderingStaffOrders> logger, CancellationToken cancellationToken)
    {
        var note = Trimmed(request.Note);
        if (ReasonErrors(request.Version, request.Reason is null, note, request.Reason == OrderingCancelReason.Other) is { } invalid)
        {
            return Task.FromResult(invalid);
        }

        return ActAsync(
            orderId, new OrderingStaffAction(OrderingOrderAction.Cancel, request.Version!.Value, CancelReason: request.Reason, Note: note),
            httpContext, organization, orders, clock, logger, cancellationToken);
    }

    internal static async Task<IResult> TablesAsync(
        IOrganizationContext organization, OrderingStaffOrders orders, OrderingDineInTables tables, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await orders.ShopAsync(organizationId, cancellationToken) is null)
        {
            return NotConfigured();
        }

        var now = clock.GetUtcNow();
        var sessions = await tables.OpenSessionsAsync(organizationId, cancellationToken);
        var members = await tables.MembersAsync(organizationId, sessions, cancellationToken);
        return Results.Ok(new OrderingStaffTablesView(now, [.. sessions.Select(session => OrderingStaffTableSessionView.Of(session, members))]));
    }

    internal static async Task<IResult> CloseSessionAsync(
        Guid sessionId, OrderingCloseSessionRequest request, HttpContext httpContext, IOrganizationContext organization,
        OrderingStaffOrders orders, OrderingDineInTables tables, TimeProvider clock, ILogger<OrderingStaffOrders> logger,
        CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId || AccountClaims.GetAccountId(httpContext.User) is not { } accountId)
        {
            return ApiErrors.Unauthorized();
        }

        if (request.Version is not { } version)
        {
            return MissingVersion();
        }

        if (await orders.ShopAsync(organizationId, cancellationToken) is null)
        {
            return NotConfigured();
        }

        var result = await tables.CloseAsync(organizationId, sessionId, version, accountId, clock.GetUtcNow(), cancellationToken);
        if (result.Session is not { } entry)
        {
            return ApiErrors.NotFound(ForbiddenReason.Ordering);
        }

        var view = OrderingStaffTableSessionView.Of(entry, await tables.MembersAsync(organizationId, [entry], cancellationToken));
        if (result.Outcome == OrderingCloseOutcome.Closed)
        {
            logger.LogInformation(
                "Table session {SessionId} of organization {OrganizationId} closed by account {AccountId}: {Orders} orders, {Completed} completed, NT${Total}.",
                sessionId, organizationId, accountId, entry.Orders.Count, result.Completed!.Count, view.Total);
            return Results.Ok(view);
        }

        var pending = result.Outcome == OrderingCloseOutcome.HasPending;
        logger.LogInformation(
            "Table session {SessionId} of organization {OrganizationId}: close by account {AccountId} refused ({Outcome}).",
            sessionId, organizationId, accountId, result.Outcome);
        return Results.Json(
            new OrderingStaffTableConflict(
                "https://tools.ietf.org/html/rfc9110#section-15.5.10",
                "Conflict",
                StatusCodes.Status409Conflict,
                pending ? "table-has-pending" : "session-changed",
                pending ? "這桌還有待確認的訂單，請先接單或婉拒再結帳。" : "這桌剛剛有新的變動，請確認金額後再結帳。",
                view),
            contentType: ProblemJson,
            statusCode: StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> PauseAsync(
        OrderingPauseRequest request, IOrganizationContext organization, OrderingStaffOrders orders, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId)
        {
            return ApiErrors.Unauthorized();
        }

        if (request.Paused is not { } paused)
        {
            return Invalid("paused", "請指定是否暫停接單。");
        }

        var now = clock.GetUtcNow();
        if (!await orders.SetPausedAsync(organizationId, paused, now, cancellationToken))
        {
            return NotConfigured();
        }

        return await SummaryAsync(organization, orders, clock, cancellationToken);
    }

    internal static async Task<IResult> AsapBandAsync(
        OrderingAsapBandRequest request, IOrganizationContext organization, OrderingStaffOrders orders, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId)
        {
            return ApiErrors.Unauthorized();
        }

        if (request.Band is not { } band || !Enum.IsDefined(band))
        {
            return Invalid("band", "請選擇準備時間。");
        }

        if (!await orders.SetAsapPrepBandAsync(organizationId, band, clock.GetUtcNow(), cancellationToken))
        {
            return NotConfigured();
        }

        return await SummaryAsync(organization, orders, clock, cancellationToken);
    }

    internal static async Task<IResult> ItemsAsync(
        IOrganizationContext organization, OrderingStaffOrders orders, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await orders.ShopAsync(organizationId, cancellationToken) is not var (_, menu))
        {
            return NotConfigured();
        }

        var now = clock.GetUtcNow();
        var states = await orders.ItemStatesAsync(organizationId, cancellationToken);
        return Results.Ok(new OrderingStaffMenuView(
            now,
            menu.Version,
            [
                .. menu.Document.Categories.Select(category => new OrderingStaffCategoryView(
                    category.Id, category.Name, [.. category.Items.Select(item => OrderingStaffItemView.Of(item, states, now))])),
            ]));
    }

    internal static async Task<IResult> ItemStateAsync(
        string itemId, OrderingItemStateRequest request, IOrganizationContext organization, OrderingStaffOrders orders, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId)
        {
            return ApiErrors.Unauthorized();
        }

        if (request.State is not { } state || !Enum.IsDefined(state))
        {
            return Invalid("state", "請指定 available、sold-out-today 或 unavailable。");
        }

        var now = clock.GetUtcNow();
        var result = await orders.SetItemStateAsync(organizationId, itemId, state, now, cancellationToken);
        return result.Outcome switch
        {
            OrderingItemStateOutcome.Done => Results.Ok(OrderingStaffItemView.Of(result.Item!, result.State is null ? [] : [result.State], now)),
            OrderingItemStateOutcome.UnknownItem => ApiErrors.WithReason(StatusCodes.Status404NotFound, "item-not-found", "目前的菜單沒有這個品項，請重新整理。"),
            _ => NotConfigured(),
        };
    }

    private static async Task<IResult> ActAsync(
        Guid orderId,
        OrderingStaffAction action,
        HttpContext httpContext,
        IOrganizationContext organization,
        OrderingStaffOrders orders,
        TimeProvider clock,
        ILogger<OrderingStaffOrders> logger,
        CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId || AccountClaims.GetAccountId(httpContext.User) is not { } accountId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await orders.ShopAsync(organizationId, cancellationToken) is null)
        {
            return NotConfigured();
        }

        var result = await orders.ActAsync(organizationId, orderId, action, accountId, clock.GetUtcNow(), cancellationToken);
        var wire = WireNames<OrderingOrderAction>.ToWire(action.Action);
        switch (result.Outcome)
        {
            case OrderingActOutcome.Done:
                var order = result.Order!;
                logger.LogInformation(
                    "Order {OrderId} ({Number}) of organization {OrganizationId}: {Action} by account {AccountId}, now {Status} at version {Version}.",
                    order.Id, OrderingOrder.FormatNumber(order.Number), organizationId, wire, accountId,
                    WireNames<OrderingOrderStatus>.ToWire(order.Status), order.Version);
                return Results.Ok(OrderingStaffOrderView.Of(order, await orders.MembersAsync(organizationId, order, cancellationToken)));
            case OrderingActOutcome.Changed:
            case OrderingActOutcome.NotAllowed:
                var current = result.Order!;
                logger.LogInformation(
                    "Order {OrderId} of organization {OrganizationId}: {Action} by account {AccountId} refused ({Outcome}), it is {Status} at version {Version}.",
                    current.Id, organizationId, wire, accountId, result.Outcome, WireNames<OrderingOrderStatus>.ToWire(current.Status), current.Version);
                var changed = result.Outcome == OrderingActOutcome.Changed;
                return Results.Json(
                    new OrderingStaffConflict(
                        "https://tools.ietf.org/html/rfc9110#section-15.5.10",
                        "Conflict",
                        StatusCodes.Status409Conflict,
                        changed ? "order-changed" : "action-not-allowed",
                        changed ? "這筆訂單剛剛被更新了，請確認目前的狀態。" : "這筆訂單目前的狀態不能這樣處理。",
                        OrderingStaffOrderView.Of(current, await orders.MembersAsync(organizationId, current, cancellationToken))),
                    contentType: ProblemJson,
                    statusCode: StatusCodes.Status409Conflict);
            case OrderingActOutcome.InvalidPromisedAt:
                return Invalid("promisedAt", result.Problem!);
            default:
                return ApiErrors.NotFound(ForbiddenReason.Ordering);
        }
    }

    private static IResult? ReasonErrors(int? version, bool reasonMissing, string? note, bool noteRequired)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (version is null)
        {
            errors["version"] = ["請重新整理後再試。"];
        }

        if (reasonMissing)
        {
            errors["reason"] = ["請選擇原因。"];
        }

        if (OrderingOrder.StaffNoteProblem(note, noteRequired) is { } problem)
        {
            errors["note"] = [problem];
        }

        return errors.Count == 0 ? null : ApiErrors.ValidationFailed(errors.Values.First()[0], errors);
    }

    private static IResult MissingVersion() => Invalid("version", "請重新整理後再試。");

    private static IResult Invalid(string field, string message) =>
        ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { [field] = [message] });

    private static IResult NotConfigured() =>
        ApiErrors.WithReason(StatusCodes.Status404NotFound, "ordering-not-configured", "這個組織還沒有設定線上點餐。");

    private static string? Trimmed(string? text) => text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
