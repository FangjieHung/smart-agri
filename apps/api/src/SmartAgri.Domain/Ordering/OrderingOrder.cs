using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Ordering;

/// <summary>How a customer gets an order (design O2). Delivery (#401) and dine-in (#402) are added beside these.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrderingServiceType>))]
public enum OrderingServiceType
{
    /// <summary>外帶預約: picked up at a chosen time slot (<see cref="OrderingSlots.Scheduled"/>).</summary>
    [JsonStringEnumMemberName("takeout-scheduled")]
    TakeoutScheduled,

    /// <summary>外帶即時: as soon as possible (<see cref="OrderingSlots.Asap"/>).</summary>
    [JsonStringEnumMemberName("takeout-asap")]
    TakeoutAsap,
}

/// <summary>Where an <see cref="OrderingOrder"/> is (design D5). Slice O2 (#398) reaches only
/// <see cref="Pending"/> and <see cref="Cancelled"/>; the staff's steps come with #399, the expiry with #400.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrderingOrderStatus>))]
public enum OrderingOrderStatus
{
    /// <summary>待確認: sent by the customer; nobody of the shop answered yet.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>已接單 (#399).</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    /// <summary>可取餐 (#399).</summary>
    [JsonStringEnumMemberName("ready")]
    Ready,

    /// <summary>完成 (#399).</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>已婉拒 (#399).</summary>
    [JsonStringEnumMemberName("declined")]
    Declined,

    /// <summary>顧客已取消: only while <see cref="Pending"/>.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    /// <summary>逾時未處理 (#400).</summary>
    [JsonStringEnumMemberName("expired")]
    Expired,
}

/// <summary>One line of an order as stored (design D6): the server's <see cref="PricedLine"/> — names and prices as the
/// menu had them — and the customer's comment for it.</summary>
public sealed record OrderingOrderLine(
    string ItemId,
    string ItemName,
    int ItemPrice,
    int Quantity,
    int UnitPrice,
    int LineTotal,
    IReadOnlyList<ChosenOption> Options,
    string? Comment)
{
    public static OrderingOrderLine Of(PricedLine line, string? comment)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new OrderingOrderLine(line.ItemId, line.ItemName, line.ItemPrice, line.Quantity, line.UnitPrice, line.LineTotal, line.Options, comment);
    }
}

/// <summary>
/// 訂單 (table <c>OrderingOrders</c>, online-ordering design D2, D4–D6, D9, Slice O2, #398): one order a customer sent from
/// the shop's ordering page — how and when it is picked up, what was ordered (a priced snapshot), who to call, and where it
/// is. The columns the later slices need (who handled it, why it was declined) come with them.
/// </summary>
/// <remarks>
/// <para>
/// The customer's name, phone, Email and comments are personal data the platform processes for the shop: never logged,
/// never in an activity. (Deleting them after the pickup time is #400, design D12.)
/// </para>
/// <para>
/// <see cref="Number"/> counts the orders of a shop's business day <see cref="BusinessDate"/> from 1 (shown as
/// <c>#001</c>, <see cref="FormatNumber"/>); the persistence layer hands it out under the shop's row lock, and
/// <c>(OrganizationId, BusinessDate, Number)</c> is unique. The status page's address carries a random token
/// (<see cref="NewStatusToken"/>) given to the customer once; only its hash is stored (<see cref="StatusTokenHash"/>).
/// <see cref="SubmissionKey"/> is the client's one-time key (a UUID), unique per shop, so a form sent twice is one order.
/// Every change of <see cref="Status"/> is a compare-and-set on (<see cref="Status"/>, <see cref="Version"/>).
/// </para>
/// </remarks>
public sealed class OrderingOrder : IOrganizationScoped
{
    /// <summary>The most pending orders one phone number may have at one shop whose pickup is still ahead (design D2).</summary>
    public const int MaxPendingPerPhone = 3;

    /// <summary>The most lines of one order.</summary>
    public const int MaxLines = 30;

    /// <summary>The highest order total (NT$): the same as one line's (<see cref="MenuLimits.MaxLineTotal"/>).</summary>
    public const int MaxTotal = MenuLimits.MaxLineTotal;

    /// <summary>Characters of a status token (32 random bytes, base64url without padding).</summary>
    public const int StatusTokenLength = 43;

    /// <summary>Characters of a stored <see cref="StatusTokenHash"/> (SHA-256 as lower-case hex).</summary>
    public const int StatusTokenHashLength = 64;

    /// <summary>Characters of a <see cref="SubmissionKey"/> (a UUID, <c>D</c> format, lower case).</summary>
    public const int SubmissionKeyLength = 36;

    /// <summary>For EF Core materialization.</summary>
    private OrderingOrder()
    {
        CustomerName = string.Empty;
        CustomerPhone = string.Empty;
        Lines = [];
        StatusTokenHash = string.Empty;
        SubmissionKey = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public OrderingServiceType ServiceType { get; private set; }

    public OrderingOrderStatus Status { get; private set; }

    /// <summary>1 at first; one more with every change of <see cref="Status"/>.</summary>
    public int Version { get; private set; }

    /// <summary>The shop's business day the order is picked up on: a scheduled time's (<see cref="OrderingSlot.Date"/>), or
    /// for ASAP the day of the ordering period it was sent in.</summary>
    public DateOnly BusinessDate { get; private set; }

    /// <summary>1, 2, … within the shop's <see cref="BusinessDate"/>.</summary>
    public int Number { get; private set; }

    /// <summary>When it is to be picked up, in UTC: the chosen time (scheduled), or for ASAP the end of the estimate
    /// (<see cref="CreatedAt"/> + <see cref="AsapMaxMinutes"/>).</summary>
    public DateTimeOffset PickupAt { get; private set; }

    /// <summary>The ASAP estimate's lower bound in minutes when it was sent; <see langword="null"/> for a scheduled order.</summary>
    public int? AsapMinMinutes { get; private set; }

    /// <summary>The ASAP estimate's upper bound; <see langword="null"/> for a scheduled order.</summary>
    public int? AsapMaxMinutes { get; private set; }

    public string CustomerName { get; private set; }

    /// <summary>Digits only, Taiwanese form (<see cref="ReservationContact.NormalizePhone"/>).</summary>
    public string CustomerPhone { get; private set; }

    public string? CustomerEmail { get; private set; }

    /// <summary>訂單備註; only when the shop asks for it (<see cref="OrderingShop.ShowOrderComment"/>).</summary>
    public string? OrderComment { get; private set; }

    /// <summary>Whether tableware is needed; <see langword="null"/> when the shop does not ask
    /// (<see cref="OrderingShop.ShowTableware"/>).</summary>
    public bool? Tableware { get; private set; }

    /// <summary>What was ordered, priced by the server.</summary>
    public IReadOnlyList<OrderingOrderLine> Lines { get; private set; }

    /// <summary>The sum of the lines' totals.</summary>
    public int Subtotal { get; private set; }

    /// <summary>What the customer pays (= <see cref="Subtotal"/>; #401 adds a delivery fee).</summary>
    public int Total { get; private set; }

    /// <summary>The menu version (<see cref="OrderingMenu.Version"/>) it was priced on.</summary>
    public int MenuVersion { get; private set; }

    /// <summary><see cref="HashStatusToken"/> of the status page's token; unique.</summary>
    public string StatusTokenHash { get; private set; }

    /// <summary>The client's one-time key; unique per organization.</summary>
    public string SubmissionKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the customer cancelled it; set exactly when <see cref="Status"/> is
    /// <see cref="OrderingOrderStatus.Cancelled"/>.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>The customer may still cancel it (design D5: only before the shop answers).</summary>
    public bool CanCancel => Status == OrderingOrderStatus.Pending;

    /// <summary>
    /// A new pending order sent at <paramref name="now"/>. Everything is already checked (the slot, the menu, the
    /// customer's details); this only guards against a caller's mistake.
    /// </summary>
    /// <param name="pickupAt">The scheduled time; ignored for ASAP, whose pickup is <paramref name="now"/> + the estimate's
    /// upper bound.</param>
    /// <param name="asap">The ASAP estimate; required exactly for <see cref="OrderingServiceType.TakeoutAsap"/>.</param>
    public static OrderingOrder Submit(
        Guid organizationId,
        OrderingServiceType serviceType,
        DateOnly businessDate,
        int number,
        DateTimeOffset pickupAt,
        AsapEstimate? asap,
        string customerName,
        string customerPhone,
        string? customerEmail,
        string? orderComment,
        bool? tableware,
        IReadOnlyList<OrderingOrderLine> lines,
        int menuVersion,
        string statusToken,
        string submissionKey,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An order always belongs to an organization.", nameof(organizationId));
        }

        if (!Enum.IsDefined(serviceType) || (serviceType == OrderingServiceType.TakeoutAsap) != (asap is not null))
        {
            throw new ArgumentException("An ASAP order has an estimate and a scheduled one has none.", nameof(asap));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(menuVersion, 1);
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count is 0 or > MaxLines)
        {
            throw new ArgumentException($"An order has 1 to {MaxLines} lines.", nameof(lines));
        }

        if (OrderingContact.NameProblem(customerName) is not null
            || ReservationContact.NormalizePhone(customerPhone) != customerPhone
            || (customerEmail is not null && ReservationContact.EmailProblem(customerEmail) is not null)
            || (orderComment is not null && OrderingContact.OrderCommentProblem(orderComment) is not null)
            || lines.Any(line => line.Comment is not null && OrderingContact.LineCommentProblem(line.Comment) is not null))
        {
            throw new ArgumentException("The customer's details are checked before an order is made.");
        }

        if (!IsStatusToken(statusToken))
        {
            throw new ArgumentException("Not a status token.", nameof(statusToken));
        }

        if (NormalizeSubmissionKey(submissionKey) != submissionKey)
        {
            throw new ArgumentException("Not a submission key.", nameof(submissionKey));
        }

        var subtotal = lines.Sum(line => (long)line.LineTotal);
        if (subtotal > MaxTotal)
        {
            throw new ArgumentException($"An order totals at most NT${MaxTotal}.", nameof(lines));
        }

        return new OrderingOrder
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            ServiceType = serviceType,
            Status = OrderingOrderStatus.Pending,
            Version = 1,
            BusinessDate = businessDate,
            Number = number,
            // Npgsql writes timestamptz only from UTC.
            PickupAt = (asap is null ? pickupAt : now.AddMinutes(asap.MaxMinutes)).ToUniversalTime(),
            AsapMinMinutes = asap?.MinMinutes,
            AsapMaxMinutes = asap?.MaxMinutes,
            CustomerName = customerName,
            CustomerPhone = customerPhone,
            CustomerEmail = customerEmail,
            OrderComment = orderComment,
            Tableware = tableware,
            Lines = [.. lines],
            Subtotal = (int)subtotal,
            Total = (int)subtotal,
            MenuVersion = menuVersion,
            StatusTokenHash = HashStatusToken(statusToken),
            SubmissionKey = submissionKey,
            CreatedAt = now.ToUniversalTime(),
        };
    }

    /// <summary>「#001」: at least three digits.</summary>
    public static string FormatNumber(int number) => "#" + number.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>A new status token: 32 random bytes, base64url without padding.</summary>
    public static string NewStatusToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Exactly <see cref="StatusTokenLength"/> characters of base64url.</summary>
    public static bool IsStatusToken(string? token) =>
        token is { Length: StatusTokenLength }
        && token.All(character => character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>The stored form of <paramref name="token"/>: SHA-256 of its ASCII bytes, as 64 lower-case hex digits.</summary>
    public static string HashStatusToken(string token)
    {
        if (!IsStatusToken(token))
        {
            throw new ArgumentException("Not a status token.", nameof(token));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    /// <summary>A client's submission key as stored: a UUID in lower-case <c>D</c> format; <see langword="null"/> when
    /// <paramref name="key"/> is not a UUID (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>) or is the empty one.</summary>
    public static string? NormalizeSubmissionKey(string? key) =>
        key is { Length: SubmissionKeyLength } && Guid.TryParseExact(key, "D", out var guid) && guid != Guid.Empty
            ? guid.ToString("D")
            : null;

    /// <summary>The shop's business day at <paramref name="now"/> for an ASAP order: the day of the ordering period
    /// <paramref name="now"/> is in (an overnight period's small hours belong to the day it opened), else the date.</summary>
    public static DateOnly AsapBusinessDate(BusinessHours hours, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hours);
        var today = hours.DateAt(now);
        foreach (var date in new[] { today.AddDays(-1), today })
        {
            if (hours.PeriodsOn(date).Any(period => period.Opens <= now && now < period.Closes))
            {
                return date;
            }
        }

        return today;
    }
}

/// <summary>
/// The customer's details at checkout (#398): each check gives the message for its field, or <see langword="null"/>
/// when it is fine. Phone and Email follow the reservation page's rules (<see cref="ReservationContact"/>). Values are
/// trimmed before they are checked.
/// </summary>
public static class OrderingContact
{
    public const int NameMaxLength = 40;

    /// <summary>Longest comment on one line (品項備註).</summary>
    public const int LineCommentMaxLength = 100;

    /// <summary>Longest order comment (訂單備註).</summary>
    public const int OrderCommentMaxLength = 200;

    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "請填寫姓名。";
        }

        if (name.Length > NameMaxLength)
        {
            return $"姓名最多 {NameMaxLength} 個字。";
        }

        return name.Any(char.IsControl) ? "姓名不能有換行或特殊字元。" : null;
    }

    public static string? LineCommentProblem(string? comment) => CommentProblem(comment, LineCommentMaxLength, "品項備註");

    public static string? OrderCommentProblem(string? comment) => CommentProblem(comment, OrderCommentMaxLength, "訂單備註");

    private static string? CommentProblem(string? comment, int maxLength, string label)
    {
        if (string.IsNullOrEmpty(comment))
        {
            return null;
        }

        if (comment.Length > maxLength)
        {
            return $"{label}最多 {maxLength} 個字（目前 {comment.Length} 個）。";
        }

        return comment.Any(character => character != '\n' && char.IsControl(character)) ? $"{label}不能有特殊字元。" : null;
    }
}
