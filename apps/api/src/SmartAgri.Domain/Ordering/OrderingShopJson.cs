using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Ordering;

/// <summary>One thing wrong with an ordering import file: where (a JSON path such as
/// <c>$.menu.categories[0].items[2].price</c>) and what, in Chinese for the operator.</summary>
public sealed record OrderingImportError(string Path, string Message)
{
    /// <summary>「<c>$.shop.slug</c>：…」.</summary>
    public override string ToString() => $"{Path}：{Message}";
}

/// <summary>A valid import file: the shop's settings and its menu.</summary>
public sealed record OrderingImport(OrderingShopSettings Shop, MenuDocument Menu);

/// <summary>The outcome of <see cref="OrderingShopJson.Parse"/>: the import when the file is valid, else every error.</summary>
public sealed record OrderingImportParseResult(OrderingImport? Import, IReadOnlyList<OrderingImportError> Errors)
{
    public bool IsValid => Import is not null && Errors.Count == 0;
}

/// <summary>
/// The online-ordering import file (online-ordering design O4, Slice O1, #397): what the operator imports with
/// <c>ordering-shop import</c>. <see cref="Parse"/> validates everything and lists every error with its JSON path; a menu is
/// stored as <see cref="WriteMenu"/>'s compact JSON (<c>OrderingMenus.Document</c>) and read back with
/// <see cref="ReadMenu"/>. Example: <c>deploy/ordering-shop.example.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>shop</c>: <c>slug</c> (<see cref="ShopReservationSettings.IsValidSlug"/>), <c>name</c> (one line, at most
/// <see cref="OrderingShopSettings.NameMaxLength"/>), <c>timeZone</c> (IANA, optional, Asia/Taipei), <c>phone</c>
/// (<see cref="ShopReservationSettings.IsValidPhone"/>), optional <c>address</c>, optional <c>notices</c>
/// <c>{ reminder?, checkout? }</c>, <c>services</c> <c>{ takeoutScheduled, takeoutAsap }</c> (at least one on),
/// <c>orderingHours</c> (the 統籌助理 settings' <c>weeklyHours</c> format and rules), and optional <c>slotMinutes</c>,
/// <c>daysAhead</c>, <c>minLeadMinutes</c>, <c>maxOrdersPerSlot</c> (or <c>null</c>), <c>showOrderComment</c>,
/// <c>showTableware</c>.
/// </para>
/// <para>
/// <c>menu</c>: <c>categories</c> <c>[{ id, name, description?, availability? { days?, from?, to? }, items }]</c>; an item
/// <c>{ id, name, price, description?, imageUrl?, optionGroups? }</c>; a group <c>{ id, name, min, max, repeatable?,
/// options }</c>; an option <c>{ id, name, price?, optionGroups? }</c>, at most <see cref="MenuLimits.MaxGroupLevels"/>
/// group levels. Ids are <see cref="MenuLimits.IsValidId"/>; prices whole NT$ from 0 to <see cref="MenuLimits.MaxPrice"/>;
/// <c>0 ≤ min ≤ max</c>, <c>1 ≤ max ≤ </c><see cref="MenuLimits.MaxCount"/>, and in a group that is not repeatable both at
/// most its number of options.
/// </para>
/// <para>Unknown and repeated fields are errors everywhere. That a slug is not another shop's is checked by the import.</para>
/// </remarks>
public static partial class OrderingShopJson
{
    private const string RootPath = "$";

    private static readonly string[] RootFields = ["shop", "menu"];

    private static readonly string[] ShopFields =
    [
        "slug", "name", "timeZone", "phone", "address", "notices", "services", "orderingHours", "slotMinutes", "daysAhead",
        "minLeadMinutes", "maxOrdersPerSlot", "showOrderComment", "showTableware",
    ];

    private static readonly string[] NoticeFields = ["reminder", "checkout"];

    private static readonly string[] ServiceFields = ["takeoutScheduled", "takeoutAsap"];

    private static readonly string[] MenuFields = ["categories"];

    private static readonly string[] CategoryFields = ["id", "name", "description", "availability", "items"];

    private static readonly string[] AvailabilityFields = ["days", "from", "to"];

    private static readonly string[] ItemFields = ["id", "name", "price", "description", "imageUrl", "optionGroups"];

    private static readonly string[] GroupFields = ["id", "name", "min", "max", "repeatable", "options"];

    private static readonly string[] OptionFields = ["id", "name", "price", "optionGroups"];

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Reads and validates an import file.</summary>
    public static OrderingImportParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException exception)
        {
            return new OrderingImportParseResult(
                null,
                [
                    new OrderingImportError(
                        RootPath,
                        $"不是有效的 JSON：第 {exception.LineNumber + 1} 行第 {exception.BytePositionInLine + 1} 個位元組附近有錯。"),
                ]);
        }

        using (document)
        {
            var reader = new Reader(strict: true);
            var import = reader.Root(document.RootElement);
            return reader.Errors.Count == 0 && import is not null
                ? new OrderingImportParseResult(import, [])
                : new OrderingImportParseResult(null, reader.Errors);
        }
    }

    /// <summary><paramref name="menu"/> as compact JSON in the file's format, written the same way every time (the stored
    /// <c>jsonb</c>, and what tells whether two menus differ): optional fields only when set, days Monday first.</summary>
    public static string WriteMenu(MenuDocument menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Compact))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("categories");
            foreach (var category in menu.Categories)
            {
                Write(writer, category);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads a stored menu. Only its shape is checked, not the rules (limits, uniqueness): those were checked when
    /// it was imported, and old versions stay readable if a rule tightens later. Throws <see cref="FormatException"/> when
    /// the shape is wrong.</summary>
    public static MenuDocument ReadMenu(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var reader = new Reader(strict: false);
        var menu = reader.Menu(document.RootElement, Child(RootPath, "menu"));
        return reader.Errors.Count == 0 && menu is not null
            ? menu
            : throw new FormatException($"The stored menu is not a menu: {string.Join("; ", reader.Errors.Select(error => error.Path))}");
    }

    private static void Write(Utf8JsonWriter writer, MenuCategory category)
    {
        writer.WriteStartObject();
        writer.WriteString("id", category.Id);
        writer.WriteString("name", category.Name);
        if (category.Description is { } description)
        {
            writer.WriteString("description", description);
        }

        if (category.Availability is { } availability)
        {
            writer.WriteStartObject("availability");
            if (availability.Days is { } days)
            {
                writer.WriteStartArray("days");
                foreach (var day in MenuLimits.Days.Where(days.Contains))
                {
                    writer.WriteStringValue(WeeklyHours.WireName(day));
                }

                writer.WriteEndArray();
            }

            if (availability.From is { } from)
            {
                writer.WriteString("from", ShopAnnouncements.FormatTime(from));
            }

            if (availability.To is { } to)
            {
                writer.WriteString("to", ShopAnnouncements.FormatTime(to));
            }

            writer.WriteEndObject();
        }

        writer.WriteStartArray("items");
        foreach (var item in category.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.Id);
            writer.WriteString("name", item.Name);
            writer.WriteNumber("price", item.Price);
            if (item.Description is { } itemDescription)
            {
                writer.WriteString("description", itemDescription);
            }

            if (item.ImageUrl is { } imageUrl)
            {
                writer.WriteString("imageUrl", imageUrl);
            }

            Write(writer, item.OptionGroups);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter writer, IReadOnlyList<OptionGroup> groups)
    {
        if (groups.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("optionGroups");
        foreach (var group in groups)
        {
            writer.WriteStartObject();
            writer.WriteString("id", group.Id);
            writer.WriteString("name", group.Name);
            writer.WriteNumber("min", group.Min);
            writer.WriteNumber("max", group.Max);
            if (group.Repeatable)
            {
                writer.WriteBoolean("repeatable", true);
            }

            writer.WriteStartArray("options");
            foreach (var option in group.Options)
            {
                writer.WriteStartObject();
                writer.WriteString("id", option.Id);
                writer.WriteString("name", option.Name);
                writer.WriteNumber("price", option.Price);
                Write(writer, option.OptionGroups);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary><paramref name="name"/> under <paramref name="path"/>: <c>.name</c>, or <c>['name']</c> when it is not an
    /// identifier.</summary>
    private static string Child(string path, string name) =>
        Identifier().IsMatch(name) ? $"{path}.{name}" : $"{path}['{name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}']";

    private static string Item(string path, int index) => $"{path}[{index}]";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    [GeneratedRegex("^(?:[01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex ClockTime();

    /// <summary>
    /// One read of an import file or a stored menu, best effort so that every error is found: structural errors (types,
    /// unknown, repeated or missing fields, malformed times) always; the rules (limits, ranges, uniqueness, combinations)
    /// only when <c>strict</c>. A part with a structural error comes back <see langword="null"/>.
    /// </summary>
    private sealed class Reader(bool strict)
    {
        private int _items;

        public List<OrderingImportError> Errors { get; } = [];

        public OrderingImport? Root(JsonElement element)
        {
            var properties = Properties(element, RootPath, RootFields);
            if (properties is null)
            {
                return null;
            }

            var shop = properties.TryGetValue("shop", out var shopElement)
                ? Shop(shopElement, Child(RootPath, "shop"))
                : Missing<OrderingShopSettings>(Child(RootPath, "shop"));
            var menu = properties.TryGetValue("menu", out var menuElement)
                ? Menu(menuElement, Child(RootPath, "menu"))
                : Missing<MenuDocument>(Child(RootPath, "menu"));
            return shop is not null && menu is not null ? new OrderingImport(shop, menu) : null;
        }

        private OrderingShopSettings? Shop(JsonElement element, string path)
        {
            var properties = Properties(element, path, ShopFields);
            if (properties is null)
            {
                return null;
            }

            var slug = RequiredString(properties, path, "slug");
            if (slug is not null && !ShopReservationSettings.IsValidSlug(slug))
            {
                Rule(
                    Child(path, "slug"),
                    $"必須是 {ShopReservationSettings.SlugMinLength}–{ShopReservationSettings.SlugMaxLength} 個小寫英文字母、數字或 -，"
                    + "不能以 - 開頭或結尾，例如 wangji；它會出現在點餐網址 /order/{代號} 裡。");
            }

            var name = RequiredString(properties, path, "name");
            if (name is not null)
            {
                SingleLine(name, Child(path, "name"), OrderingShopSettings.NameMaxLength);
            }

            var timeZoneId = ShopSettings.DefaultTimeZoneId;
            var complete = slug is not null && name is not null;
            if (properties.TryGetValue("timeZone", out var timeZone))
            {
                var timeZonePath = Child(path, "timeZone");
                if (TextOf(timeZone, timeZonePath) is { } id)
                {
                    timeZoneId = id;
                    if (!ShopSettingsJson.IsValidTimeZoneId(id))
                    {
                        Rule(timeZonePath, $"不認得的時區「{id}」；請用 IANA 時區名稱，例如 {ShopSettings.DefaultTimeZoneId}。");
                    }
                }
                else
                {
                    complete = false;
                }
            }

            var phone = RequiredString(properties, path, "phone");
            complete &= phone is not null;
            if (phone is not null && !ShopReservationSettings.IsValidPhone(phone))
            {
                Rule(Child(path, "phone"), "必須是店家的電話號碼（8 到 15 個數字，可以有空白、-、( )，開頭可以是 +，最多 20 個字元），例如 02-2345-6789。");
            }

            var (address, addressRead) = OptionalText(properties, path, "address", OrderingShopSettings.AddressMaxLength, multiLine: false);
            complete &= addressRead;

            string? reminder = null;
            string? checkout = null;
            if (properties.TryGetValue("notices", out var noticesElement) && noticesElement.ValueKind != JsonValueKind.Null)
            {
                var noticesPath = Child(path, "notices");
                var notices = Properties(noticesElement, noticesPath, NoticeFields);
                if (notices is null)
                {
                    complete = false;
                }
                else
                {
                    (reminder, var reminderRead) = OptionalText(notices, noticesPath, "reminder", OrderingShopSettings.NoticeMaxLength, multiLine: true);
                    (checkout, var checkoutRead) = OptionalText(notices, noticesPath, "checkout", OrderingShopSettings.NoticeMaxLength, multiLine: true);
                    complete &= reminderRead && checkoutRead;
                }
            }

            OrderingServices? services = null;
            var servicesPath = Child(path, "services");
            if (!properties.TryGetValue("services", out var servicesElement))
            {
                Error(servicesPath, "缺少這個欄位，例如 { \"takeoutScheduled\": true, \"takeoutAsap\": true }。");
            }
            else if (Properties(servicesElement, servicesPath, ServiceFields) is { } serviceProperties)
            {
                var scheduled = RequiredBoolean(serviceProperties, servicesPath, "takeoutScheduled");
                var asap = RequiredBoolean(serviceProperties, servicesPath, "takeoutAsap");
                if (scheduled is not null && asap is not null)
                {
                    services = new OrderingServices(scheduled.Value, asap.Value);
                    if (!services.Any)
                    {
                        Rule(servicesPath, "至少要開放一種取餐方式（暫停接單請在後台切換）。");
                    }
                }
            }

            complete &= services is not null;

            WeeklyHours? hours = null;
            var hoursPath = Child(path, "orderingHours");
            if (!properties.TryGetValue("orderingHours", out var hoursElement))
            {
                Error(hoursPath, "缺少這個欄位：每週可點餐（取餐）的時間，格式同店家設定的 weeklyHours。");
            }
            else
            {
                var hoursErrors = new List<ShopSettingsError>();
                hours = ShopSettingsJson.ParseWeeklyHours(hoursElement, hoursPath, hoursErrors);
                Errors.AddRange(hoursErrors.Select(error => new OrderingImportError(error.Path, error.Message)));
            }

            complete &= hours is not null;

            var slotMinutes = Integer(properties, path, "slotMinutes", OrderingShopSettings.DefaultSlotMinutes, 1, 60);
            if (slotMinutes is { } slot && slot is >= 1 and <= 60 && !ShopReservationSettings.SlotMinuteChoices.Contains(slot))
            {
                Rule(Child(path, "slotMinutes"), $"必須是 {string.Join('、', ShopReservationSettings.SlotMinuteChoices)} 其中之一（分鐘）。");
            }

            var daysAhead = Integer(properties, path, "daysAhead", OrderingShopSettings.DefaultDaysAhead, 1, OrderingShopSettings.MaxDaysAhead);
            var minLead = Integer(properties, path, "minLeadMinutes", OrderingShopSettings.DefaultMinLeadMinutes, 0, OrderingShopSettings.MaxMinLeadMinutes);
            int? maxOrders = null;
            var maxOrdersRead = true;
            if (properties.TryGetValue("maxOrdersPerSlot", out var maxOrdersElement) && maxOrdersElement.ValueKind != JsonValueKind.Null)
            {
                maxOrders = Integer(properties, path, "maxOrdersPerSlot", 0, 1, OrderingShopSettings.MaxMaxOrdersPerSlot);
                maxOrdersRead = maxOrders is not null;
            }

            var showComment = OptionalBoolean(properties, path, "showOrderComment", fallback: true);
            var showTableware = OptionalBoolean(properties, path, "showTableware", fallback: true);
            complete &= slotMinutes is not null && daysAhead is not null && minLead is not null && maxOrdersRead
                && showComment is not null && showTableware is not null;
            return complete
                ? new OrderingShopSettings(
                    slug!, name!, timeZoneId, phone!, address, reminder, checkout, services!, hours!, slotMinutes!.Value,
                    daysAhead!.Value, minLead!.Value, maxOrders, showComment!.Value, showTableware!.Value)
                : null;
        }

        public MenuDocument? Menu(JsonElement element, string path)
        {
            var properties = Properties(element, path, MenuFields);
            if (properties is null)
            {
                return null;
            }

            var categoriesPath = Child(path, "categories");
            if (!properties.TryGetValue("categories", out var list))
            {
                Error(categoriesPath, "缺少這個欄位。");
                return null;
            }

            if (Array(list, categoriesPath) is not { } elements)
            {
                return null;
            }

            if (elements.Count == 0)
            {
                Rule(categoriesPath, "至少要有一個分類。");
            }
            else if (elements.Count > MenuLimits.MaxCategories)
            {
                Rule(categoriesPath, $"最多 {MenuLimits.MaxCategories} 個分類（目前 {elements.Count} 個）。");
            }

            _items = 0;
            var categoryIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var itemIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var categories = new List<MenuCategory>();
            var complete = true;
            for (var index = 0; index < elements.Count; index++)
            {
                if (Category(elements[index], Item(categoriesPath, index), categoryIds, itemIds) is { } category)
                {
                    categories.Add(category);
                }
                else
                {
                    complete = false;
                }
            }

            if (_items > MenuLimits.MaxItems)
            {
                Rule(categoriesPath, $"整份菜單最多 {MenuLimits.MaxItems} 個品項（目前 {_items} 個）。");
            }

            return complete ? new MenuDocument(categories) : null;
        }

        private MenuCategory? Category(
            JsonElement element, string path, Dictionary<string, string> categoryIds, Dictionary<string, string> itemIds)
        {
            var properties = Properties(element, path, CategoryFields);
            if (properties is null)
            {
                return null;
            }

            var id = Id(properties, path, categoryIds, "分類");
            var name = Name(properties, path);
            var (description, descriptionRead) = OptionalText(properties, path, "description", MenuLimits.DescriptionMaxLength, multiLine: true);
            MenuAvailability? availability = null;
            var availabilityRead = true;
            if (properties.TryGetValue("availability", out var availabilityElement) && availabilityElement.ValueKind != JsonValueKind.Null)
            {
                availability = Availability(availabilityElement, Child(path, "availability"));
                availabilityRead = availability is not null;
            }

            var itemsPath = Child(path, "items");
            List<MenuItem>? items = null;
            if (!properties.TryGetValue("items", out var itemsElement))
            {
                Error(itemsPath, "缺少這個欄位。");
            }
            else if (Array(itemsElement, itemsPath) is { } elements)
            {
                if (elements.Count == 0)
                {
                    Rule(itemsPath, "至少要有一個品項。");
                }

                _items += elements.Count;
                items = [];
                for (var index = 0; index < elements.Count; index++)
                {
                    if (MenuItem(elements[index], Item(itemsPath, index), itemIds) is { } item)
                    {
                        items?.Add(item);
                    }
                    else
                    {
                        items = null;
                    }
                }
            }

            return id is not null && name is not null && descriptionRead && availabilityRead && items is not null
                ? new MenuCategory(id, name, description, availability, items)
                : null;
        }

        private MenuAvailability? Availability(JsonElement element, string path)
        {
            var properties = Properties(element, path, AvailabilityFields);
            if (properties is null)
            {
                return null;
            }

            var complete = true;
            List<DayOfWeek>? days = null;
            if (properties.TryGetValue("days", out var daysElement))
            {
                var daysPath = Child(path, "days");
                if (Array(daysElement, daysPath) is { } elements)
                {
                    days = [];
                    if (elements.Count == 0)
                    {
                        Rule(daysPath, "至少要有一天；每天都供應請拿掉 days。");
                    }

                    for (var index = 0; index < elements.Count; index++)
                    {
                        var dayPath = Item(daysPath, index);
                        var text = TextOf(elements[index], dayPath);
                        var day = text is null ? (DayOfWeek?)null : MenuLimits.Days.FirstOrDefault(candidate => WeeklyHours.WireName(candidate) == text, (DayOfWeek)(-1));
                        if (day is null)
                        {
                            complete = false;
                        }
                        else if ((int)day.Value < 0)
                        {
                            Error(dayPath, "必須是 mon、tue、wed、thu、fri、sat 或 sun。");
                            complete = false;
                        }
                        else if (days.Contains(day.Value))
                        {
                            Rule(dayPath, $"「{text}」重複。");
                        }
                        else
                        {
                            days.Add(day.Value);
                        }
                    }
                }
                else
                {
                    complete = false;
                }
            }

            var (from, fromRead) = OptionalTime(properties, path, "from");
            var (to, toRead) = OptionalTime(properties, path, "to");
            complete &= fromRead && toRead;
            if (from is not null && from == to)
            {
                Rule(path, "from 和 to 相同；跨午夜的時段把 to 寫得比 from 早，例如 22:00 到 02:00。");
            }

            if (complete && days is null && from is null && to is null)
            {
                Rule(path, "至少要有 days、from 或 to 其中之一；全天供應請拿掉 availability。");
            }

            return complete ? new MenuAvailability(days, from, to) : null;
        }

        private MenuItem? MenuItem(JsonElement element, string path, Dictionary<string, string> itemIds)
        {
            var properties = Properties(element, path, ItemFields);
            if (properties is null)
            {
                return null;
            }

            var id = Id(properties, path, itemIds, "品項");
            var name = Name(properties, path);
            var price = Price(properties, path, required: true);
            var (description, descriptionRead) = OptionalText(properties, path, "description", MenuLimits.DescriptionMaxLength, multiLine: true);
            string? imageUrl = null;
            var imageRead = true;
            if (properties.TryGetValue("imageUrl", out var imageElement) && imageElement.ValueKind != JsonValueKind.Null)
            {
                var imagePath = Child(path, "imageUrl");
                imageUrl = TextOf(imageElement, imagePath);
                imageRead = imageUrl is not null;
                if (imageUrl is not null)
                {
                    if (imageUrl.Length > ShopSettingsJson.ImageUrlMaxLength)
                    {
                        Rule(imagePath, $"最多 {ShopSettingsJson.ImageUrlMaxLength} 個字元（目前 {imageUrl.Length} 個）。");
                    }
                    else if (!ShopSettingsJson.IsHttpsUrl(imageUrl))
                    {
                        Rule(imagePath, "必須是完整的 https:// 網址（公開的圖片位址）。");
                    }
                }
            }

            var groups = Groups(properties, path, level: 1);
            return id is not null && name is not null && price is not null && descriptionRead && imageRead && groups is not null
                ? new MenuItem(id, name, price.Value, description, imageUrl, groups)
                : null;
        }

        /// <summary>The <c>optionGroups</c> of an item (level 1) or an option (one more than its group); empty when absent.</summary>
        private List<OptionGroup>? Groups(Dictionary<string, JsonElement> properties, string path, int level)
        {
            if (!properties.TryGetValue("optionGroups", out var element) || element.ValueKind == JsonValueKind.Null)
            {
                return [];
            }

            var groupsPath = Child(path, "optionGroups");
            if (Array(element, groupsPath) is not { } elements)
            {
                return null;
            }

            if (elements.Count > 0 && level > MenuLimits.MaxGroupLevels)
            {
                Rule(groupsPath, $"選項群組最多 {MenuLimits.MaxGroupLevels} 層，這裡是第 {level} 層。");
                return strict ? null : [];
            }

            if (elements.Count > MenuLimits.MaxGroupsPerParent)
            {
                Rule(groupsPath, $"最多 {MenuLimits.MaxGroupsPerParent} 個選項群組（目前 {elements.Count} 個）。");
            }

            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            List<OptionGroup>? groups = [];
            for (var index = 0; index < elements.Count; index++)
            {
                if (Group(elements[index], Item(groupsPath, index), ids, level) is { } group)
                {
                    groups?.Add(group);
                }
                else
                {
                    groups = null;
                }
            }

            return groups;
        }

        private OptionGroup? Group(JsonElement element, string path, Dictionary<string, string> groupIds, int level)
        {
            var properties = Properties(element, path, GroupFields);
            if (properties is null)
            {
                return null;
            }

            var id = Id(properties, path, groupIds, "選項群組");
            var name = Name(properties, path);
            var min = RequiredInteger(properties, path, "min", 0, MenuLimits.MaxCount);
            var max = RequiredInteger(properties, path, "max", 1, MenuLimits.MaxCount);
            var repeatable = OptionalBoolean(properties, path, "repeatable", fallback: false);

            var optionsPath = Child(path, "options");
            List<MenuOption>? options = null;
            if (!properties.TryGetValue("options", out var optionsElement))
            {
                Error(optionsPath, "缺少這個欄位。");
            }
            else if (Array(optionsElement, optionsPath) is { } elements)
            {
                if (elements.Count == 0)
                {
                    Rule(optionsPath, "至少要有一個選項。");
                }
                else if (elements.Count > MenuLimits.MaxOptionsPerGroup)
                {
                    Rule(optionsPath, $"最多 {MenuLimits.MaxOptionsPerGroup} 個選項（目前 {elements.Count} 個）。");
                }

                var optionIds = new Dictionary<string, string>(StringComparer.Ordinal);
                options = [];
                for (var index = 0; index < elements.Count; index++)
                {
                    if (Option(elements[index], Item(optionsPath, index), optionIds, level) is { } option)
                    {
                        options?.Add(option);
                    }
                    else
                    {
                        options = null;
                    }
                }

                if (min is { } least && max is { } most)
                {
                    if (least > most)
                    {
                        Rule(path, $"min（{least}）不能大於 max（{most}）。");
                    }

                    if (repeatable == false && elements.Count > 0)
                    {
                        if (least > elements.Count)
                        {
                            Rule(Child(path, "min"), $"不能重複選的群組，min（{least}）不能大於選項數（{elements.Count}）。");
                        }

                        if (most > elements.Count)
                        {
                            Rule(Child(path, "max"), $"不能重複選的群組，max（{most}）不能大於選項數（{elements.Count}）；要讓同一個選項選多份請設 \"repeatable\": true。");
                        }
                    }
                }
            }

            return id is not null && name is not null && min is not null && max is not null && repeatable is not null && options is not null
                ? new OptionGroup(id, name, min.Value, max.Value, repeatable.Value, options)
                : null;
        }

        private MenuOption? Option(JsonElement element, string path, Dictionary<string, string> optionIds, int level)
        {
            var properties = Properties(element, path, OptionFields);
            if (properties is null)
            {
                return null;
            }

            var id = Id(properties, path, optionIds, "選項");
            var name = Name(properties, path);
            var price = Price(properties, path, required: false);
            var groups = Groups(properties, path, level + 1);
            return id is not null && name is not null && price is not null && groups is not null
                ? new MenuOption(id, name, price.Value, groups)
                : null;
        }

        /// <summary>A required <c>id</c>, unique among <paramref name="seen"/> (id → path of its first use).</summary>
        private string? Id(Dictionary<string, JsonElement> properties, string path, Dictionary<string, string> seen, string kind)
        {
            var idPath = Child(path, "id");
            if (RequiredString(properties, path, "id") is not { } id)
            {
                return null;
            }

            if (!MenuLimits.IsValidId(id))
            {
                Rule(idPath, $"必須是 1–{MenuLimits.IdMaxLength} 個小寫英文字母、數字或 -，例如 beef-noodle；訂單用它認得{kind}，之後不要改。");
            }
            else if (seen.TryGetValue(id, out var first))
            {
                Rule(idPath, $"「{id}」和 {first} 的{kind} id 重複。");
            }
            else
            {
                seen.Add(id, path);
            }

            return id;
        }

        private string? Name(Dictionary<string, JsonElement> properties, string path)
        {
            var name = RequiredString(properties, path, "name");
            if (name is not null)
            {
                SingleLine(name, Child(path, "name"), MenuLimits.NameMaxLength);
            }

            return name;
        }

        /// <summary>A price in whole NT$, 0 to <see cref="MenuLimits.MaxPrice"/>; 0 when optional and absent.</summary>
        private int? Price(Dictionary<string, JsonElement> properties, string path, bool required)
        {
            var pricePath = Child(path, "price");
            if (!properties.TryGetValue("price", out var value))
            {
                if (required)
                {
                    Error(pricePath, "缺少這個欄位。");
                    return null;
                }

                return 0;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var price))
            {
                Error(pricePath, "必須是整數（新台幣元，不能有小數）。");
                return null;
            }

            if (price < 0)
            {
                Rule(pricePath, $"不能是負數（目前 {price}）。");
            }
            else if (price > MenuLimits.MaxPrice)
            {
                Rule(pricePath, $"最多 {MenuLimits.MaxPrice} 元（目前 {price}）。");
            }

            return price;
        }

        /// <summary>An optional text (absent or <c>null</c> = none), one line or several; read = no structural error.</summary>
        private (string? Value, bool Read) OptionalText(
            Dictionary<string, JsonElement> properties, string path, string name, int maxLength, bool multiLine)
        {
            if (!properties.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return (null, true);
            }

            var propertyPath = Child(path, name);
            if (TextOf(value, propertyPath) is not { } text)
            {
                return (null, false);
            }

            if (multiLine)
            {
                MultiLine(text, propertyPath, maxLength);
            }
            else
            {
                SingleLine(text, propertyPath, maxLength);
            }

            return (text, true);
        }

        private (TimeOnly? Value, bool Read) OptionalTime(Dictionary<string, JsonElement> properties, string path, string name)
        {
            if (!properties.TryGetValue(name, out var value))
            {
                return (null, true);
            }

            var propertyPath = Child(path, name);
            if (TextOf(value, propertyPath) is not { } text)
            {
                return (null, false);
            }

            if (!ClockTime().IsMatch(text))
            {
                Error(propertyPath, "必須是 24 小時制的 HH:mm，例如 09:30。");
                return (null, false);
            }

            return (new TimeOnly(int.Parse(text[..2], CultureInfo.InvariantCulture), int.Parse(text[3..], CultureInfo.InvariantCulture)), true);
        }

        private void SingleLine(string value, string path, int maxLength)
        {
            if (!strict)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                Error(path, "不能是空白。");
                return;
            }

            if (value.Length > maxLength)
            {
                Error(path, $"最多 {maxLength} 個字（目前 {value.Length} 個）。");
            }

            if (value.Any(char.IsControl))
            {
                Error(path, "不能有換行或控制字元。");
            }
            else if (value != value.Trim())
            {
                Error(path, "前後不能有空白。");
            }
        }

        private void MultiLine(string value, string path, int maxLength)
        {
            if (!strict)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                Error(path, "不能是空白；不需要時請拿掉這個欄位。");
                return;
            }

            if (value.Length > maxLength)
            {
                Error(path, $"最多 {maxLength} 個字（目前 {value.Length} 個）。");
            }

            if (value.Any(character => character != '\n' && char.IsControl(character)))
            {
                Error(path, "除了換行（\\n）之外不能有控制字元。");
            }
        }

        private List<JsonElement>? Array(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                Error(path, "必須是陣列（[ … ]）。");
                return null;
            }

            return [.. element.EnumerateArray()];
        }

        /// <summary>The object's properties, reporting unknown and repeated ones; <see langword="null"/> when it is not an
        /// object.</summary>
        private Dictionary<string, JsonElement>? Properties(JsonElement element, string path, IReadOnlyCollection<string> allowed)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, "必須是物件（{ … }）。");
                return null;
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = Child(path, property.Name);
                if (!allowed.Contains(property.Name))
                {
                    Error(propertyPath, $"不認得的欄位；這裡可以有：{string.Join('、', allowed)}。");
                }
                else if (!properties.TryAdd(property.Name, property.Value))
                {
                    Error(propertyPath, "欄位重複。");
                }
            }

            return properties;
        }

        private string? RequiredString(Dictionary<string, JsonElement> properties, string path, string name) =>
            properties.TryGetValue(name, out var value) ? TextOf(value, Child(path, name)) : Missing<string>(Child(path, name));

        private bool? RequiredBoolean(Dictionary<string, JsonElement> properties, string path, string name)
        {
            var propertyPath = Child(path, name);
            if (!properties.TryGetValue(name, out var value))
            {
                Error(propertyPath, "缺少這個欄位。");
                return null;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            Error(propertyPath, "必須是 true 或 false。");
            return null;
        }

        private bool? OptionalBoolean(Dictionary<string, JsonElement> properties, string path, string name, bool fallback) =>
            properties.ContainsKey(name) ? RequiredBoolean(properties, path, name) : fallback;

        /// <summary>An optional whole number (<paramref name="fallback"/> when absent) from <paramref name="min"/> to
        /// <paramref name="max"/> (the range is a rule); <see langword="null"/> when it is not a whole number.</summary>
        private int? Integer(Dictionary<string, JsonElement> properties, string path, string name, int fallback, int min, int max) =>
            properties.ContainsKey(name) ? RequiredInteger(properties, path, name, min, max) : fallback;

        private int? RequiredInteger(Dictionary<string, JsonElement> properties, string path, string name, int min, int max)
        {
            var propertyPath = Child(path, name);
            if (!properties.TryGetValue(name, out var value))
            {
                Error(propertyPath, "缺少這個欄位。");
                return null;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            {
                Error(propertyPath, "必須是整數。");
                return null;
            }

            if (number < min || number > max)
            {
                Rule(propertyPath, $"必須是 {min} 到 {max} 的整數（目前 {number}）。");
            }

            return number;
        }

        private string? TextOf(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            Error(path, "必須是字串。");
            return null;
        }

        private T? Missing<T>(string path)
            where T : class
        {
            Error(path, "缺少這個欄位。");
            return null;
        }

        private void Error(string path, string message) => Errors.Add(new OrderingImportError(path, message));

        private void Rule(string path, string message)
        {
            if (strict)
            {
                Error(path, message);
            }
        }
    }
}
