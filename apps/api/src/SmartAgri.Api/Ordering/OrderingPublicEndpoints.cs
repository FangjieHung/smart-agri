using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Ordering;
using SmartAgri.Infrastructure.Ordering;

namespace SmartAgri.Api.Ordering;

/// <summary>
/// The anonymous online-ordering API the customer app (<c>apps/shop</c>, served at <c>/order/</c>) calls (online-ordering
/// design D1, D2, D4, D6, D9, Slice O2, #398), under <see cref="Prefix"/>. The full contract — shapes, reasons, polling — is
/// §4.2 of <c>docs/plans/2026-11-06-online-ordering.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous (a sign-in cookie sent along decides nothing), under <c>/api/v1/public</c> so a foreign <c>Origin</c> is refused
/// (<see cref="PublicOriginGuard"/>), in the OpenAPI document so the app can generate its types. Every answer is
/// <c>Cache-Control: no-store</c> and <c>Referrer-Policy: no-referrer</c>. Rate
/// limits per client IP, every shop together: sending an order (<see cref="PublicRateLimitedEndpoint.OrderingSubmission"/>)
/// and everything else (<see cref="PublicRateLimitedEndpoint.Ordering"/>); beyond them <c>429 rate-limited</c> with
/// <c>Retry-After</c>.
/// </para>
/// <para>
/// The status token is sent only in the <see cref="TokenHeader"/> request header, never in a path or query: ASP.NET Core
/// puts the request path into every log scope (exported over OTLP) and traces record <c>url.path</c>, while request headers
/// are recorded by neither (no HTTP logging is registered; the ASP.NET Core trace instrumentation adds no header tags). The
/// customer's status page keeps it in the URL fragment (<c>/order/{slug}/o#&lt;token&gt;</c>), which browsers never send.
/// </para>
/// <para>
/// An unknown slug, a shop without a menu and an order token of another shop are the same <c>404</c>. Refusals are
/// <see cref="OrderingProblem"/>s. Logs carry the organization, the order id, its number and counts — never the customer's
/// name, phone, Email or comments, nor the token.
/// </para>
/// </remarks>
public static class OrderingPublicEndpoints
{
    public const string Prefix = PublicOriginGuard.PathPrefix + "/ordering/{slug}";

    /// <summary>Largest request body (bytes): 30 lines with four levels of options fit many times over.</summary>
    public const long MaxBodyBytes = 64 * 1024;

    /// <summary>The request header that carries an order's status token.</summary>
    public const string TokenHeader = "X-Ordering-Token";

    private const string Problem = "application/problem+json";

    public static IEndpointRouteBuilder MapOrderingPublicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var shop = endpoints.MapGroup(Prefix)
            .AllowAnonymous()
            .WithTags("Ordering (public)")
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                http.User = new ClaimsPrincipal(new ClaimsIdentity());
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                return await next(context);
            });

        shop.MapGet(string.Empty, (string slug, HttpContext context, OrderingPublicFlow flow) => flow.ShopAsync(slug, context.RequestAborted))
            .WithPublicRateLimit(PublicRateLimitedEndpoint.Ordering)
            .WithSummary("A shop's ordering page: the shop, whether it takes orders now and how")
            .WithDescription("Anonymous. 404 shop-not-found for an unknown slug or a shop without a menu.")
            .Produces<OrderingShopInfoView>()
            .Produces<OrderingProblem>(StatusCodes.Status404NotFound, Problem)
            .Produces(StatusCodes.Status429TooManyRequests);

        shop.MapGet("/menu", (string slug, HttpContext context, OrderingPublicFlow flow) => flow.MenuAsync(slug, context.RequestAborted))
            .WithPublicRateLimit(PublicRateLimitedEndpoint.Ordering)
            .WithSummary("The shop's current menu, with the items that cannot be ordered")
            .Produces<OrderingMenuView>()
            .Produces<OrderingProblem>(StatusCodes.Status404NotFound, Problem)
            .Produces(StatusCodes.Status429TooManyRequests);

        shop.MapGet("/slots", (string slug, HttpContext context, OrderingPublicFlow flow) => flow.SlotsAsync(slug, context.RequestAborted))
            .WithPublicRateLimit(PublicRateLimitedEndpoint.Ordering)
            .WithSummary("The business days and times a scheduled takeout order can be picked up")
            .Produces<OrderingSlotsView>()
            .Produces<OrderingProblem>(StatusCodes.Status404NotFound, Problem)
            .Produces(StatusCodes.Status429TooManyRequests);

        shop.MapPost("/orders", (string slug, OrderingOrderRequest request, HttpContext context, OrderingPublicFlow flow) =>
                flow.SubmitAsync(slug, request, context.RequestAborted))
            .WithPublicRateLimit(PublicRateLimitedEndpoint.OrderingSubmission)
            .WithMetadata(new BodySizeLimit(MaxBodyBytes))
            .WithSummary("Send an order: priced by the server, never by the client")
            .WithDescription(
                "201 with the status token (given only here) and the order. 400 (the framework's answer) when the body is not JSON of this shape. " +
                "404 shop-not-found. 409 duplicate-submission (orderNumber), shop-paused, service-unavailable, slot-unavailable, " +
                "slot-full, menu-changed (menuVersion), items-unavailable (lineErrors). 422 invalid-order (errors, lineErrors). " +
                "429 too-many-pending, or rate-limited with Retry-After.")
            .Accepts<OrderingOrderRequest>("application/json")
            .Produces<OrderingSubmittedView>(StatusCodes.Status201Created)
            .Produces<OrderingProblem>(StatusCodes.Status404NotFound, Problem)
            .Produces<OrderingProblem>(StatusCodes.Status409Conflict, Problem)
            .Produces<OrderingProblem>(StatusCodes.Status422UnprocessableEntity, Problem)
            .Produces<OrderingProblem>(StatusCodes.Status429TooManyRequests, Problem);

        shop.MapGet("/orders/current", (string slug, [FromHeader(Name = TokenHeader)] string? token, HttpContext context, OrderingPublicFlow flow) =>
                flow.OrderAsync(slug, token, context.RequestAborted))
            .WithPublicRateLimit(PublicRateLimitedEndpoint.Ordering)
            .WithSummary("The order of the status token in the X-Ordering-Token header (the status page polls it)")
            .WithDescription("404 shop-not-found, or order-not-found for a missing or malformed header or a token the shop has no order of.")
            .Produces<OrderingOrderView>()
            .Produces<OrderingProblem>(StatusCodes.Status404NotFound, Problem)
            .Produces(StatusCodes.Status429TooManyRequests);

        shop.MapPost("/orders/current/cancel", (string slug, [FromHeader(Name = TokenHeader)] string? token, HttpContext context, OrderingPublicFlow flow) =>
                flow.CancelAsync(slug, token, context.RequestAborted))
            .WithPublicRateLimit(PublicRateLimitedEndpoint.Ordering)
            .WithSummary("Cancel an order while the shop has not answered it")
            .WithDescription("200 with the cancelled order. 409 not-cancellable (orderStatus) once it is anything but pending. 404 as GET.")
            .Produces<OrderingOrderView>()
            .Produces<OrderingProblem>(StatusCodes.Status404NotFound, Problem)
            .Produces<OrderingProblem>(StatusCodes.Status409Conflict, Problem)
            .Produces(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    private sealed class BodySizeLimit(long bytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => bytes;
    }
}

/// <summary>The steps of <see cref="OrderingPublicEndpoints"/> (#398).</summary>
public sealed class OrderingPublicFlow
{
    private readonly OrderingLookup _lookup;
    private readonly OrderingShops _shops;
    private readonly OrderingOrders _orders;
    private readonly TimeProvider _clock;
    private readonly ILogger<OrderingPublicFlow> _logger;

    public OrderingPublicFlow(OrderingLookup lookup, OrderingShops shops, OrderingOrders orders, TimeProvider clock, ILogger<OrderingPublicFlow> logger)
    {
        _lookup = lookup;
        _shops = shops;
        _orders = orders;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IResult> ShopAsync(string slug, CancellationToken cancellationToken)
    {
        if (await FindAsync(slug, cancellationToken) is not { } found)
        {
            return ShopNotFound();
        }

        return Results.Ok(OrderingPublicViews.Shop(found.Shop, found.Menu!.Version, _clock.GetUtcNow()));
    }

    public async Task<IResult> MenuAsync(string slug, CancellationToken cancellationToken)
    {
        if (await FindAsync(slug, cancellationToken) is not { } found)
        {
            return ShopNotFound();
        }

        var states = await _orders.ItemStatesAsync(found.Shop.OrganizationId, cancellationToken);
        return Results.Ok(OrderingPublicViews.Menu(found.Menu!, states, _clock.GetUtcNow()));
    }

    public async Task<IResult> SlotsAsync(string slug, CancellationToken cancellationToken)
    {
        if (await FindAsync(slug, cancellationToken) is not { } found)
        {
            return ShopNotFound();
        }

        var now = _clock.GetUtcNow();
        var counts = await _orders.SlotCountsAsync(found.Shop.OrganizationId, now, cancellationToken);
        return Results.Ok(OrderingPublicViews.Slots(OrderingSlots.Scheduled(found.Shop, now, counts), now));
    }

    public async Task<IResult> OrderAsync(string slug, string? token, CancellationToken cancellationToken)
    {
        if (await FindAsync(slug, cancellationToken) is not { } found)
        {
            return ShopNotFound();
        }

        return await _orders.FindByTokenAsync(found.Shop.OrganizationId, token, cancellationToken) is { } order
            ? Results.Ok(OrderingOrderView.Of(order))
            : OrderNotFound();
    }

    public async Task<IResult> CancelAsync(string slug, string? token, CancellationToken cancellationToken)
    {
        if (await FindAsync(slug, cancellationToken) is not { } found)
        {
            return ShopNotFound();
        }

        var organizationId = found.Shop.OrganizationId;
        var result = await _orders.CancelAsync(organizationId, token, _clock.GetUtcNow(), cancellationToken);
        switch (result.Outcome)
        {
            case OrderingCancelOutcome.Cancelled:
                _logger.LogInformation("Order {OrderId} of organization {OrganizationId} was cancelled by its customer.", result.Order!.Id, organizationId);
                return Results.Ok(OrderingOrderView.Of(result.Order));
            case OrderingCancelOutcome.NotCancellable:
                return Refuse(
                    StatusCodes.Status409Conflict, "not-cancellable", "店家已經處理這筆訂單，無法取消；如需更改請直接聯絡店家。",
                    orderStatus: result.Order!.Status);
            default:
                return OrderNotFound();
        }
    }

    public async Task<IResult> SubmitAsync(string slug, OrderingOrderRequest request, CancellationToken cancellationToken)
    {
        if (await FindAsync(slug, cancellationToken) is not { } found)
        {
            return ShopNotFound();
        }

        var shop = found.Shop;
        var organizationId = shop.OrganizationId;
        if (Check(shop, request) is not { } submission)
        {
            var errors = FieldErrors(shop, request);
            _logger.LogInformation(
                "An order for organization {OrganizationId} was refused: {Fields}.", organizationId, string.Join(", ", errors.Keys.Order(StringComparer.Ordinal)));
            return Refuse(StatusCodes.Status422UnprocessableEntity, "invalid-order", errors.Values.First()[0], errors: errors);
        }

        var result = await _orders.SubmitAsync(submission, _clock.GetUtcNow(), cancellationToken);
        if (result.Outcome != OrderingSubmitOutcome.Stored)
        {
            _logger.LogInformation(
                "An order for organization {OrganizationId} was refused: {Outcome}.", organizationId, result.Outcome);
        }

        switch (result.Outcome)
        {
            case OrderingSubmitOutcome.Stored:
                var order = result.Order!;
                _logger.LogInformation(
                    "Order {OrderId} ({Number}) of organization {OrganizationId} received: {ServiceType}, {Lines} lines, NT${Total}.",
                    order.Id, OrderingOrder.FormatNumber(order.Number), organizationId, WireNames<OrderingServiceType>.ToWire(order.ServiceType),
                    order.Lines.Count, order.Total);
                return Results.Json(
                    new OrderingSubmittedView(result.Token!, OrderingOrderView.Of(order)), statusCode: StatusCodes.Status201Created);
            case OrderingSubmitOutcome.Duplicate:
                var number = OrderingOrder.FormatNumber(result.Order!.Number);
                return Refuse(
                    StatusCodes.Status409Conflict, "duplicate-submission", $"這筆訂單已經送出過了（訂單編號 {number}）。", orderNumber: number);
            case OrderingSubmitOutcome.Paused:
                return Refuse(StatusCodes.Status409Conflict, "shop-paused", $"店家暫停接受線上點餐，請稍後再試或來電 {shop.Phone}。");
            case OrderingSubmitOutcome.ServiceUnavailable:
                return Refuse(
                    StatusCodes.Status409Conflict, "service-unavailable",
                    submission.ServiceType == OrderingServiceType.TakeoutAsap ? "現在無法接受儘快取餐的訂單，請改選預約取餐時間。" : "店家目前不接受預約取餐。");
            case OrderingSubmitOutcome.SlotUnavailable:
                return Refuse(StatusCodes.Status409Conflict, "slot-unavailable", "這個取餐時間已經不能選了，請重新選擇。");
            case OrderingSubmitOutcome.SlotFull:
                return Refuse(StatusCodes.Status409Conflict, "slot-full", "這個取餐時間已經額滿，請選擇其他時間。");
            case OrderingSubmitOutcome.MenuChanged:
                return Refuse(
                    StatusCodes.Status409Conflict, "menu-changed", "菜單剛剛更新了，請確認購物車裡的品項與價格後再送出。", menuVersion: result.MenuVersion);
            case OrderingSubmitOutcome.InvalidLines:
                return Refuse(
                    StatusCodes.Status422UnprocessableEntity, "invalid-order", "購物車裡有品項的選項不正確，請重新選擇。", lineErrors: result.LineErrors);
            case OrderingSubmitOutcome.TotalTooLarge:
                var message = $"一筆訂單最多 NT${OrderingOrder.MaxTotal.ToString("N0", CultureInfo.InvariantCulture)}。";
                return Refuse(StatusCodes.Status422UnprocessableEntity, "invalid-order", message, errors: new Dictionary<string, string[]> { ["lines"] = [message] });
            case OrderingSubmitOutcome.ItemsUnavailable:
                return Refuse(
                    StatusCodes.Status409Conflict, "items-unavailable", "購物車裡有品項在取餐時間無法供應，請移除後再送出。", lineErrors: result.LineErrors);
            default:
                return Refuse(
                    StatusCodes.Status429TooManyRequests, "too-many-pending",
                    $"這支電話已經有 {OrderingOrder.MaxPendingPerPhone} 筆等待店家確認的訂單，請等店家處理後再訂，或來電 {shop.Phone}。");
        }
    }

    /// <summary>The submission when every field is fine; otherwise <see langword="null"/> (<see cref="FieldErrors"/>
    /// says why).</summary>
    private static OrderingSubmission? Check(OrderingShop shop, OrderingOrderRequest request)
    {
        if (FieldErrors(shop, request).Count > 0)
        {
            return null;
        }

        var customer = request.Customer!;
        return new OrderingSubmission(
            shop.OrganizationId,
            request.ServiceType!.Value,
            request.ServiceType == OrderingServiceType.TakeoutScheduled ? ParseDate(request.PickupDate) : null,
            request.ServiceType == OrderingServiceType.TakeoutScheduled ? request.PickupSlot : null,
            request.MenuVersion!.Value,
            [.. request.Lines!.Select(line => new OrderingLineInput(
                line.ItemId, line.Quantity, line.Selections?.Select(selection => selection.ToSelection()).ToList(), Trimmed(line.Comment)))],
            customer.Name!.Trim(),
            ReservationContact.NormalizePhone(customer.Phone)!,
            Trimmed(customer.Email),
            shop.ShowOrderComment ? Trimmed(request.OrderComment) : null,
            shop.ShowTableware ? request.Tableware ?? false : null,
            OrderingOrder.NormalizeSubmissionKey(request.SubmissionKey)!);
    }

    /// <summary>Every field's problem, by field: what can be checked without the shop's lock and menu.</summary>
    private static Dictionary<string, string[]> FieldErrors(OrderingShop shop, OrderingOrderRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string field, string? problem)
        {
            if (problem is not null)
            {
                errors[field] = [problem];
            }
        }

        Add("submissionKey", OrderingOrder.NormalizeSubmissionKey(request.SubmissionKey) is null ? "請重新整理頁面後再送出。" : null);
        Add("serviceType", request.ServiceType is null ? "請選擇取餐方式。" : null);
        if (request.ServiceType == OrderingServiceType.TakeoutScheduled && (ParseDate(request.PickupDate) is null || string.IsNullOrEmpty(request.PickupSlot)))
        {
            Add("pickupSlot", "請選擇取餐時間。");
        }

        Add("menuVersion", request.MenuVersion is null ? "請重新整理頁面後再送出。" : null);
        if (request.Lines is not { Count: > 0 } lines)
        {
            Add("lines", "購物車是空的。");
        }
        else if (lines.Count > OrderingOrder.MaxLines)
        {
            Add("lines", $"一筆訂單最多 {OrderingOrder.MaxLines} 項。");
        }
        else
        {
            for (var index = 0; index < lines.Count; index++)
            {
                Add($"lines[{index}].comment", lines[index] is null ? "購物車的內容不正確。" : OrderingContact.LineCommentProblem(Trimmed(lines[index].Comment)));
            }
        }

        var customer = request.Customer;
        Add("customer.name", OrderingContact.NameProblem(customer?.Name?.Trim()));
        Add("customer.phone", ReservationContact.PhoneProblem(customer?.Phone));
        Add("customer.email", ReservationContact.EmailProblem(Trimmed(customer?.Email)));
        if (shop.ShowOrderComment)
        {
            Add("orderComment", OrderingContact.OrderCommentProblem(Trimmed(request.OrderComment)));
        }

        return errors;
    }

    /// <summary>The shop of <paramref name="slug"/> and its current menu; <see langword="null"/> for an unknown slug or a
    /// shop without a menu.</summary>
    private async Task<OrderingShopView?> FindAsync(string slug, CancellationToken cancellationToken)
    {
        if (ShopReservationSettings.NormalizeSlug(slug) is not { } normalized
            || await _lookup.FindShopAsync(normalized, cancellationToken) is not { } organizationId
            || await _shops.FindAsync(organizationId, cancellationToken) is not { Menu: not null } found)
        {
            return null;
        }

        return found;
    }

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static string? Trimmed(string? text) =>
        text?.ReplaceLineEndings("\n").Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static IResult ShopNotFound() =>
        Refuse(StatusCodes.Status404NotFound, "shop-not-found", "找不到這家店的線上點餐頁面，請確認網址。");

    private static IResult OrderNotFound() =>
        Refuse(StatusCodes.Status404NotFound, "order-not-found", "找不到這筆訂單，請確認網址。");

    private static IResult Refuse(
        int status,
        string reason,
        string message,
        IReadOnlyDictionary<string, string[]>? errors = null,
        IReadOnlyList<OrderingLineError>? lineErrors = null,
        int? menuVersion = null,
        string? orderNumber = null,
        OrderingOrderStatus? orderStatus = null)
    {
        var (type, title) = status switch
        {
            StatusCodes.Status404NotFound => ("https://tools.ietf.org/html/rfc9110#section-15.5.5", "Not Found"),
            StatusCodes.Status409Conflict => ("https://tools.ietf.org/html/rfc9110#section-15.5.10", "Conflict"),
            StatusCodes.Status422UnprocessableEntity => ("https://tools.ietf.org/html/rfc9110#section-15.5.21", "Unprocessable Content"),
            _ => ("https://tools.ietf.org/html/rfc6585#section-4", "Too Many Requests"),
        };
        return Results.Json(
            new OrderingProblem(type, title, status, reason, message, errors, lineErrors, menuVersion, orderNumber, orderStatus),
            contentType: "application/problem+json",
            statusCode: status);
    }
}
