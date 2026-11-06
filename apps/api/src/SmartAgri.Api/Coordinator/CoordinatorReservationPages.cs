using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The reservation page's HTML (#389): mobile first, Traditional Chinese, one inline style and one inline script allowed
/// by their hashes, nothing loaded, no frames, the form posting to this origin only. The form works without JavaScript —
/// 〔查看這天的時段〕 posts it back with the values kept and the chosen date's times — and with it, choosing a date
/// changes the times at once. Every value from outside is HTML-encoded.
/// </summary>
public static class CoordinatorReservationPages
{
    public const string NotFoundTitle = "找不到這家店的訂位頁";

    public const string NotFoundMessage = "請確認網址是否正確，或直接打電話給店家訂位。";

    public const string TooManyRequestsMessage = "操作太頻繁了，請稍候一分鐘再試。";

    public const string UnavailableMessage = "訂位系統暫時無法使用，請稍後再試，或直接打電話給店家訂位。";

    public const string UnreadableFormMessage = "無法讀取這份表單，請重新填寫後送出。";

    public const string ResubmitMessage = "這份表單已經失效，請確認內容後再按一次〔送出訂位〕。";

    public const string DoneTitle = "已送出訂位";

    /// <summary>The customer left an Email address (and the platform can send Email).</summary>
    public const string DoneWithEmailMessage = "店家確認後會寄 Email 通知您；也可能直接來電。";

    public const string DoneWithoutEmailMessage = "店家會來電與您確認。";

    /// <summary>The field the bots fill in: off screen, out of the tab order and hidden from screen readers.</summary>
    private const string HoneypotClass = "hp";

    private const string HtmlContentType = "text/html; charset=utf-8";

    private const string Style =
        "*,*::before,*::after{box-sizing:border-box}"
        + "html{-webkit-text-size-adjust:100%}"
        + "body{margin:0;font:16px/1.6 system-ui,-apple-system,\"PingFang TC\",\"Noto Sans TC\",\"Microsoft JhengHei\",sans-serif;"
        + "color:#292524;background:#fafaf9}"
        + "main{max-width:32rem;margin:0 auto;padding:24px 16px 48px}"
        + "header{margin-bottom:20px}"
        + ".eyebrow{margin:0;color:#57534e;font-size:.875rem}"
        + "h1{font-size:1.5rem;line-height:1.3;margin:4px 0 8px;overflow-wrap:anywhere}"
        + "h2{font-size:1rem;margin:0 0 12px}"
        + "p{margin:0 0 12px;overflow-wrap:anywhere}"
        + "a{color:#166534}"
        + ".card{background:#fff;border:1px solid #e7e5e4;border-radius:12px;padding:16px;margin-bottom:16px}"
        + "fieldset{border:0;margin:0;padding:0;min-width:0}"
        + "legend{float:left;width:100%;padding:0;font-weight:600;margin-bottom:12px}"
        + "legend+*{clear:both}"
        + ".field{margin-bottom:16px}.field:last-child{margin-bottom:0}"
        + "label{display:block;font-weight:600;margin-bottom:4px}"
        + ".optional{font-weight:400;color:#57534e;font-size:.875rem}"
        + "input,select,textarea{display:block;width:100%;min-width:0;font:inherit;color:inherit;padding:10px 12px;min-height:44px;"
        + "border:1px solid #a8a29e;border-radius:8px;background:#fff}"
        + "textarea{min-height:88px;resize:vertical}"
        + "input:focus-visible,select:focus-visible,textarea:focus-visible,button:focus-visible{outline:3px solid #166534;outline-offset:2px}"
        + "[aria-invalid=true]{border-color:#b91c1c;border-width:2px}"
        + ".hint{color:#57534e;font-size:.875rem;margin:4px 0 0}"
        + ".error{color:#b91c1c;font-size:.875rem;margin:4px 0 0}"
        + "button{font:inherit;min-height:44px;padding:10px 20px;border-radius:8px;cursor:pointer}"
        + ".primary{display:block;width:100%;border:0;background:#166534;color:#fff;font-weight:600}"
        + ".secondary{margin-top:8px;border:1px solid #166534;background:#fff;color:#166534}"
        + ".alert{border:2px solid #b91c1c;border-radius:12px;background:#fef2f2;padding:12px 16px;margin-bottom:16px}"
        + ".alert ul{margin:4px 0 0;padding-left:20px}"
        + ".notice{color:#57534e;font-size:.875rem;margin-top:16px}"
        + "dl{margin:0}dt{color:#57534e;font-size:.875rem}dd{margin:0 0 12px;overflow-wrap:anywhere;white-space:pre-line}"
        + "." + HoneypotClass + "{position:absolute;left:-10000px;width:1px;height:1px;overflow:hidden}";

    /// <summary>With JavaScript: the times follow the date at once, and 〔查看這天的時段〕 is not needed.</summary>
    private const string Script =
        "(function(){var d=document.getElementById('date'),t=document.getElementById('time'),b=document.getElementById('show-times');"
        + "if(!d||!t){return;}if(b){b.hidden=true;}"
        + "d.addEventListener('change',function(){var o=d.options[d.selectedIndex],s=o?o.getAttribute('data-slots')||'':'',v=t.value,"
        + "f=t.options[0];t.textContent='';if(f){t.appendChild(f);f.selected=true;}"
        + "s.split('|').forEach(function(p){if(!p){return;}var i=p.indexOf(' '),e=document.createElement('option');"
        + "e.value=p.slice(0,i);e.textContent=p.slice(i+1);if(e.value===v){e.selected=true;}t.appendChild(e);});});})();";

    private static readonly string StyleHash = Hash(Style);

    private static readonly string ScriptHash = Hash(Script);

    /// <summary>Pages without the form.</summary>
    public static readonly string ContentSecurityPolicy =
        $"default-src 'none'; style-src 'sha256-{StyleHash}'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>The form page: its script by hash, its form posting to this origin only.</summary>
    public static readonly string FormContentSecurityPolicy =
        $"default-src 'none'; style-src 'sha256-{StyleHash}'; script-src 'sha256-{ScriptHash}'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>The headers of every answer (the <c>404</c> and <c>429</c> pages included).</summary>
    public static void SetHeaders(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Robots-Tag"] = "noindex";
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
    }

    /// <summary>An unknown slug, or a shop that does not take reservations: one page for both.</summary>
    public static IResult NotFound() =>
        Page(NotFoundTitle, null, $"<p>{NotFoundMessage}</p>", StatusCodes.Status404NotFound);

    /// <summary>Beyond the rate limit.</summary>
    public static IResult TooManyRequests() =>
        Page("請稍後再試", null, $"<p>{TooManyRequestsMessage}</p>", StatusCodes.Status429TooManyRequests);

    /// <summary>Something failed on our side.</summary>
    public static IResult Unavailable() =>
        Page("暫時無法訂位", null, $"<p>{UnavailableMessage}</p>", StatusCodes.Status503ServiceUnavailable);

    /// <summary>The phone number already has the most pending reservations (§3.1 point 5).</summary>
    public static string TooManyPendingMessage(string shopPhone) =>
        $"這支電話在本店已有 {CoordinatorReservation.MaxPendingPerPhone} 筆待確認的訂位，請等店家確認後再訂，或直接來電 {shopPhone}。";

    /// <summary>The form, with what was filled in and the messages.</summary>
    public static IResult Form(ReservationPage page, int statusCode)
    {
        ArgumentNullException.ThrowIfNull(page);
        var shop = page.Shop;
        var reservations = shop.Reservations;
        var body = new StringBuilder();
        if (page.General is { } general)
        {
            body.Append($"<div class=\"alert\" role=\"alert\"><p>{Encode(general)}</p></div>");
        }

        if (page.Errors.Count > 0)
        {
            body.Append("<div class=\"alert\" role=\"alert\" id=\"errors\"><p><strong>請修正以下欄位：</strong></p><ul>");
            foreach (var field in FieldOrder.Where(page.Errors.ContainsKey))
            {
                body.Append($"<li><a href=\"#{field}\">{Encode(page.Errors[field])}</a></li>");
            }

            body.Append("</ul></div>");
        }

        if (page.SelectedDate is not { } selected)
        {
            body.Append("<div class=\"card\"><p>目前沒有可以線上訂位的時段。</p>")
                .Append($"<p>請直接來電 {PhoneLink(reservations.Phone!)} 訂位。</p></div>");
            return Page("線上訂位", shop, body.ToString(), statusCode);
        }

        var values = page.Values;
        // No action: the page's own address, whatever its case, trailing slash or the public base URL's path.
        body.Append("<form method=\"post\">")
            .Append($"<input type=\"hidden\" name=\"{ReservationFields.SubmissionKey}\" value=\"{Encode(page.SubmissionKey)}\">");

        // When and how many.
        body.Append("<div class=\"card\"><fieldset><legend>日期與時間</legend>");
        body.Append(FieldStart(ReservationFields.Date, "日期"))
            .Append($"<select id=\"{ReservationFields.Date}\" name=\"{ReservationFields.Date}\"{Aria(page, ReservationFields.Date)}>");
        foreach (var (date, slots) in page.Dates)
        {
            var data = string.Join('|', slots.Select(slot => $"{slot.Value} {slot.Label}"));
            body.Append($"<option value=\"{DateValue(date)}\" data-slots=\"{Encode(data)}\"{(date == selected ? " selected" : string.Empty)}>")
                .Append(Encode(DateLabel(date, page.Today)))
                .Append("</option>");
        }

        body.Append("</select>")
            .Append($"<button type=\"submit\" class=\"secondary\" id=\"show-times\" name=\"{ReservationFields.Step}\" value=\"{ReservationFields.ShowTimesStep}\" formnovalidate>查看這天的時段</button>")
            .Append(FieldEnd(page, ReservationFields.Date));

        body.Append(FieldStart(ReservationFields.Time, "時間"))
            .Append($"<select id=\"{ReservationFields.Time}\" name=\"{ReservationFields.Time}\" required{Aria(page, ReservationFields.Time)}>")
            .Append("<option value=\"\">請選擇時間</option>");
        foreach (var slot in page.SlotsOn(selected))
        {
            body.Append($"<option value=\"{slot.Value}\"{(slot.Value == values.Time ? " selected" : string.Empty)}>{Encode(slot.Label)}</option>");
        }

        body.Append("</select>")
            .Append($"<p class=\"hint\" id=\"{ReservationFields.Time}-hint\">"
                + (reservations.LastSeatingMinutes > 0 ? $"最晚可訂到打烊前 {Duration(reservations.LastSeatingMinutes)}；" : string.Empty)
                + $"需在 {Duration(reservations.MinLeadMinutes)}前訂位。</p>")
            .Append(FieldEnd(page, ReservationFields.Time));

        body.Append(FieldStart(ReservationFields.PartySize, "人數"))
            .Append($"<select id=\"{ReservationFields.PartySize}\" name=\"{ReservationFields.PartySize}\" required{Aria(page, ReservationFields.PartySize)}>")
            .Append("<option value=\"\">請選擇人數</option>");
        for (var size = 1; size <= reservations.MaxPartySize; size++)
        {
            var value = size.ToString(CultureInfo.InvariantCulture);
            body.Append($"<option value=\"{value}\"{(value == values.PartySize ? " selected" : string.Empty)}>{value} 位</option>");
        }

        body.Append("</select>")
            .Append($"<p class=\"hint\" id=\"{ReservationFields.PartySize}-hint\">超過 {reservations.MaxPartySize} 位請來電 {Encode(reservations.Phone!)}。</p>")
            .Append(FieldEnd(page, ReservationFields.PartySize))
            .Append("</fieldset></div>");

        // Who.
        body.Append("<div class=\"card\"><fieldset><legend>聯絡資料</legend>");
        body.Append(FieldStart(ReservationFields.Name, "姓名"))
            .Append(Input(page, ReservationFields.Name, "text", values.Name, ReservationContact.NameMaxLength, "name", required: true))
            .Append(FieldEnd(page, ReservationFields.Name));
        body.Append(FieldStart(ReservationFields.Phone, "電話"))
            .Append(Input(page, ReservationFields.Phone, "tel", values.Phone, ReservationContact.PhoneInputMaxLength, "tel", required: true, inputMode: "tel"))
            .Append($"<p class=\"hint\" id=\"{ReservationFields.Phone}-hint\">手機或市話，例如 0912-345-678。店家會用這支電話與您聯絡。</p>")
            .Append(FieldEnd(page, ReservationFields.Phone));
        if (page.EmailOffered)
        {
            body.Append(FieldStart(ReservationFields.Email, "Email", optional: true))
                .Append(Input(page, ReservationFields.Email, "email", values.Email, ReservationContact.EmailMaxLength, "email", required: false, inputMode: "email"))
                .Append($"<p class=\"hint\" id=\"{ReservationFields.Email}-hint\">留下 Email，店家確認或婉拒時會寄信通知您。</p>")
                .Append(FieldEnd(page, ReservationFields.Email));
        }

        body.Append(FieldStart(ReservationFields.Note, "備註", optional: true))
            .Append($"<textarea id=\"{ReservationFields.Note}\" name=\"{ReservationFields.Note}\" maxlength=\"{ReservationContact.NoteMaxLength}\" rows=\"3\""
                + $"{Aria(page, ReservationFields.Note)}>")
            .Append(Encode(values.Note ?? string.Empty))
            .Append("</textarea>")
            .Append($"<p class=\"hint\" id=\"{ReservationFields.Note}-hint\">例如：需要兒童椅（最多 {ReservationContact.NoteMaxLength} 字）。</p>")
            .Append(FieldEnd(page, ReservationFields.Note))
            .Append("</fieldset>")
            .Append($"<div class=\"{HoneypotClass}\" aria-hidden=\"true\"><label for=\"{ReservationFields.Honeypot}\">網站（請留空）</label>")
            .Append($"<input type=\"text\" id=\"{ReservationFields.Honeypot}\" name=\"{ReservationFields.Honeypot}\" tabindex=\"-1\" autocomplete=\"off\"></div>")
            .Append("</div>");

        body.Append("<button type=\"submit\" class=\"primary\">送出訂位</button>")
            .Append($"<p class=\"notice\">{Notice(shop, page.EmailOffered)}</p>")
            .Append("</form>");
        return Page("線上訂位", shop, body.ToString(), statusCode, FormContentSecurityPolicy, Script);
    }

    /// <summary>「已送出訂位」: the reservation as stored, and how the shop will answer.</summary>
    public static IResult Done(ReservationPage page, ReservationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(summary);
        var shop = page.Shop;
        var phone = shop.Reservations.Phone!;
        var time = ShopAnnouncements.FormatTime(TimeOnly.FromDateTime(summary.Local))
            + (DateOnly.FromDateTime(summary.Local) != summary.Date ? "（隔天）" : string.Empty);
        var body = new StringBuilder()
            .Append("<div class=\"card\"><p>這筆訂位要等店家確認才算成立。</p><dl>")
            .Append($"<dt>店家</dt><dd>{Encode(shop.Settings.StoreName)}</dd>")
            .Append($"<dt>日期與時間</dt><dd>{Encode(ShopAnnouncements.FormatDate(summary.Date))} {Encode(time)}</dd>")
            .Append($"<dt>人數</dt><dd>{summary.PartySize.ToString(CultureInfo.InvariantCulture)} 位</dd>")
            .Append($"<dt>姓名</dt><dd>{Encode(summary.Name)}</dd>")
            .Append($"<dt>電話</dt><dd>{Encode(ReservationContact.FormatPhone(summary.Phone))}</dd>");
        if (summary.Email is { } email)
        {
            body.Append($"<dt>Email</dt><dd>{Encode(email)}</dd>");
        }

        if (summary.Note is { } note)
        {
            body.Append($"<dt>備註</dt><dd>{Encode(note)}</dd>");
        }

        body.Append("</dl></div>")
            .Append($"<p>{(summary.Email is not null && page.EmailOffered ? DoneWithEmailMessage : DoneWithoutEmailMessage)}</p>")
            .Append($"<p>如需更改或取消，請來電 {PhoneLink(phone)}。</p>");
        return Page(DoneTitle, shop, body.ToString(), StatusCodes.Status200OK);
    }

    /// <summary>The personal-data notice under the button (§3.1 point 4).</summary>
    private static string Notice(ReservationShop shop, bool emailOffered)
    {
        var store = Encode(shop.Settings.StoreName);
        return $"個人資料告知：{store}透過本訂位系統蒐集您的姓名、電話{(emailOffered ? "、Email" : string.Empty)}與備註，"
            + "只用於處理這次訂位（確認、通知與聯絡），由本系統的平台營運者代為處理。"
            + $"資料保存到訂位時間後 {CoordinatorReservation.RetentionDays} 天，之後刪除。"
            + $"您可以來電 {PhoneLink(shop.Reservations.Phone!)} 向{store}查詢、閱覽、更正或刪除您的資料；"
            + "沒有姓名與電話將無法訂位。";
    }

    /// <summary>The fields in the order of the form, for the list of errors.</summary>
    private static readonly string[] FieldOrder =
    [
        ReservationFields.Date, ReservationFields.Time, ReservationFields.PartySize, ReservationFields.Name,
        ReservationFields.Phone, ReservationFields.Email, ReservationFields.Note,
    ];

    private static string FieldStart(string field, string label, bool optional = false) =>
        $"<div class=\"field\"><label for=\"{field}\">{label}{(optional ? " <span class=\"optional\">（選填）</span>" : string.Empty)}</label>";

    private static string FieldEnd(ReservationPage page, string field) =>
        (page.Errors.TryGetValue(field, out var message) ? $"<p class=\"error\" id=\"{field}-error\">{Encode(message)}</p>" : string.Empty)
        + "</div>";

    private static string Input(
        ReservationPage page, string field, string type, string? value, int maxLength, string autocomplete, bool required, string? inputMode = null) =>
        $"<input type=\"{type}\" id=\"{field}\" name=\"{field}\" value=\"{Encode(value ?? string.Empty)}\" maxlength=\"{maxLength}\""
        + $" autocomplete=\"{autocomplete}\"{(inputMode is null ? string.Empty : $" inputmode=\"{inputMode}\"")}{(required ? " required" : string.Empty)}"
        + $"{Aria(page, field)}>";

    /// <summary>The fields with a hint under them (<c>{field}-hint</c>).</summary>
    private static bool HasHint(string field) =>
        field is ReservationFields.Time or ReservationFields.PartySize or ReservationFields.Phone or ReservationFields.Email or ReservationFields.Note;

    /// <summary><c>aria-describedby</c> (the hint and the error) and <c>aria-invalid</c> of a field.</summary>
    private static string Aria(ReservationPage page, string field)
    {
        var invalid = page.Errors.ContainsKey(field);
        var described = string.Join(' ', new[] { HasHint(field) ? $"{field}-hint" : null, invalid ? $"{field}-error" : null }.OfType<string>());
        return (described.Length > 0 ? $" aria-describedby=\"{described}\"" : string.Empty) + (invalid ? " aria-invalid=\"true\"" : string.Empty);
    }

    /// <summary>「11/8（日）」, with 「・今天」「・明天」, or 「・昨晚」 for last night's period.</summary>
    private static string DateLabel(DateOnly date, DateOnly today) =>
        ShopAnnouncements.FormatDate(date) + (date.DayNumber - today.DayNumber) switch
        {
            -1 => "・昨晚",
            0 => "・今天",
            1 => "・明天",
            _ => string.Empty,
        };

    private static string DateValue(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>「2 小時」, 「90 分鐘」, 「1 天」.</summary>
    private static string Duration(int minutes) =>
        minutes % (24 * 60) == 0 ? $"{minutes / (24 * 60)} 天"
        : minutes % 60 == 0 ? $"{minutes / 60} 小時"
        : $"{minutes} 分鐘";

    private static string PhoneLink(string phone) =>
        $"<a href=\"{Encode(ShopReservationSettings.TelUri(phone))}\">{Encode(phone)}</a>";

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string Hash(string text) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IResult Page(string title, ReservationShop? shop, string body, int statusCode, string? contentSecurityPolicy = null, string? script = null)
    {
        var header = shop is null
            ? $"<header><h1>{Encode(title)}</h1></header>"
            : $"<header><p class=\"eyebrow\">{Encode(title)}</p><h1>{Encode(shop.Settings.StoreName)}</h1>"
              + $"<p>電話 {PhoneLink(shop.Reservations.Phone!)}</p></header>";
        var documentTitle = shop is null ? title : $"{title}｜{shop.Settings.StoreName}";
        return new PageResult(
            "<!doctype html>\n<html lang=\"zh-Hant-TW\">\n<head>\n<meta charset=\"utf-8\">\n"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
            + "<meta name=\"robots\" content=\"noindex\">\n<meta name=\"referrer\" content=\"no-referrer\">\n"
            + $"<title>{Encode(documentTitle)}</title>\n<style>{Style}</style>\n</head>\n<body>\n<main>\n{header}\n{body}\n</main>\n"
            + (script is null ? string.Empty : $"<script>{script}</script>\n")
            + "</body>\n</html>\n",
            statusCode,
            contentSecurityPolicy);
    }

    private sealed class PageResult(string html, int statusCode, string? contentSecurityPolicy) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (contentSecurityPolicy is not null)
            {
                httpContext.Response.Headers.ContentSecurityPolicy = contentSecurityPolicy;
            }

            return Results.Text(html, HtmlContentType, Encoding.UTF8, statusCode).ExecuteAsync(httpContext);
        }
    }
}
