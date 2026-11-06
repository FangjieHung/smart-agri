using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Ordering;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Ordering;

/// <summary>
/// Table <c>OrderingShops</c> (online-ordering Slice O1, #397): one row per organization (unique <c>OrganizationId</c>);
/// <c>Slug</c> unique across all organizations (the anonymous ordering page finds its shop by it, #398). The ordering hours
/// are a <c>jsonb</c> object in the 統籌助理 settings' <c>weeklyHours</c> format. <c>Version</c> is the concurrency token.
/// </summary>
internal sealed class OrderingShopConfiguration : IEntityTypeConfiguration<OrderingShop>
{
    /// <summary>The unique index on <c>Slug</c>: an import that wants another shop's slug fails on it.</summary>
    public const string SlugIndex = "IX_OrderingShops_Slug";

    private static readonly ValueComparer<WeeklyHours> WeeklyHoursComparer = new(
        (left, right) => ShopSettingsJson.WriteWeeklyHours(left!) == ShopSettingsJson.WriteWeeklyHours(right!),
        hours => ShopSettingsJson.WriteWeeklyHours(hours).GetHashCode(StringComparison.Ordinal),
        hours => ShopSettingsJson.ReadWeeklyHours(ShopSettingsJson.WriteWeeklyHours(hours)));

    public void Configure(EntityTypeBuilder<OrderingShop> builder)
    {
        builder.ToTable("OrderingShops", table =>
        {
            table.HasCheckConstraint("CK_OrderingShops_OrderingHours", "jsonb_typeof(\"OrderingHours\") = 'object'");
            table.HasCheckConstraint("CK_OrderingShops_SlotMinutes", "\"SlotMinutes\" IN (10, 15, 20, 30, 60)");
            table.HasCheckConstraint(
                "CK_OrderingShops_DaysAhead", $"\"DaysAhead\" BETWEEN 1 AND {OrderingShopSettings.MaxDaysAhead}");
            table.HasCheckConstraint(
                "CK_OrderingShops_MinLeadMinutes", $"\"MinLeadMinutes\" BETWEEN 0 AND {OrderingShopSettings.MaxMinLeadMinutes}");
            table.HasCheckConstraint(
                "CK_OrderingShops_MaxOrdersPerSlot",
                $"\"MaxOrdersPerSlot\" IS NULL OR \"MaxOrdersPerSlot\" BETWEEN 1 AND {OrderingShopSettings.MaxMaxOrdersPerSlot}");
            table.HasCheckConstraint("CK_OrderingShops_Version", "\"Version\" >= 1");
        });
        builder.HasKey(shop => shop.Id);
        builder.Property(shop => shop.Id).ValueGeneratedNever();
        builder.Property(shop => shop.Slug).HasMaxLength(ShopReservationSettings.SlugMaxLength).IsRequired();
        builder.Property(shop => shop.Name).HasMaxLength(OrderingShopSettings.NameMaxLength).IsRequired();
        builder.Property(shop => shop.TimeZoneId).HasMaxLength(ShopSettings.TimeZoneIdMaxLength).IsRequired();
        builder.Property(shop => shop.Phone).HasMaxLength(ShopReservationSettings.PhoneMaxLength).IsRequired();
        builder.Property(shop => shop.Address).HasMaxLength(OrderingShopSettings.AddressMaxLength);
        builder.Property(shop => shop.ReminderNotice).HasMaxLength(OrderingShopSettings.NoticeMaxLength);
        builder.Property(shop => shop.CheckoutNotice).HasMaxLength(OrderingShopSettings.NoticeMaxLength);
        builder.Property(shop => shop.OrderingHours)
            .HasConversion(
                new ValueConverter<WeeklyHours, string>(
                    hours => ShopSettingsJson.WriteWeeklyHours(hours),
                    json => ShopSettingsJson.ReadWeeklyHours(json)),
                WeeklyHoursComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(shop => shop.AsapPrepBand)
            .HasConversion<WireNameConverter<AsapPrepBand>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(shop => shop.Version).IsConcurrencyToken();
        builder.Ignore(shop => shop.Settings);
        builder.Ignore(shop => shop.Services);
        builder.Ignore(shop => shop.Hours);
        builder.HasIndex(shop => shop.OrganizationId).IsUnique();
        builder.HasIndex(shop => shop.Slug).IsUnique().HasDatabaseName(SlugIndex);
    }
}

/// <summary>
/// Table <c>OrderingMenus</c> (#397): every version of every shop's menu, never changed; unique
/// <c>(OrganizationId, Version)</c>, the current menu being the highest. <c>Document</c> is the menu as a <c>jsonb</c>
/// object in the import file's format (<see cref="OrderingShopJson.WriteMenu"/>), read whole.
/// </summary>
internal sealed class OrderingMenuConfiguration : IEntityTypeConfiguration<OrderingMenu>
{
    /// <summary>The unique index on <c>(OrganizationId, Version)</c>: two imports adding the same version at once.</summary>
    public const string VersionIndex = "IX_OrderingMenus_OrganizationId_Version";

    // A menu is immutable records: equal when written the same, and the snapshot can be the same instance.
    private static readonly ValueComparer<MenuDocument> DocumentComparer = new(
        (left, right) => OrderingShopJson.WriteMenu(left!) == OrderingShopJson.WriteMenu(right!),
        menu => OrderingShopJson.WriteMenu(menu).GetHashCode(StringComparison.Ordinal),
        menu => menu);

    public void Configure(EntityTypeBuilder<OrderingMenu> builder)
    {
        builder.ToTable("OrderingMenus", table =>
        {
            table.HasCheckConstraint("CK_OrderingMenus_Document", "jsonb_typeof(\"Document\") = 'object'");
            table.HasCheckConstraint("CK_OrderingMenus_Version", "\"Version\" >= 1");
        });
        builder.HasKey(menu => menu.Id);
        builder.Property(menu => menu.Id).ValueGeneratedNever();
        builder.Property(menu => menu.Document)
            .HasConversion(
                new ValueConverter<MenuDocument, string>(
                    menu => OrderingShopJson.WriteMenu(menu),
                    json => OrderingShopJson.ReadMenu(json)),
                DocumentComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.HasIndex(menu => new { menu.OrganizationId, menu.Version }).IsUnique().HasDatabaseName(VersionIndex);
    }
}

/// <summary>
/// Table <c>OrderingItemStates</c> (#397): at most one row per <c>(OrganizationId, ItemId)</c>; a 今日售完 row always has
/// <c>ResetsAt</c>, a 停售 row never.
/// </summary>
internal sealed class OrderingItemStateConfiguration : IEntityTypeConfiguration<OrderingItemState>
{
    public void Configure(EntityTypeBuilder<OrderingItemState> builder)
    {
        builder.ToTable("OrderingItemStates", table =>
            table.HasCheckConstraint(
                "CK_OrderingItemStates_ResetsAt",
                "(\"Kind\" = 'sold-out-today') = (\"ResetsAt\" IS NOT NULL)"));
        builder.HasKey(state => state.Id);
        builder.Property(state => state.Id).ValueGeneratedNever();
        builder.Property(state => state.ItemId).HasMaxLength(MenuLimits.IdMaxLength).IsRequired();
        builder.Property(state => state.Kind)
            .HasConversion<WireNameConverter<OrderingItemStateKind>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.HasIndex(state => new { state.OrganizationId, state.ItemId }).IsUnique();
    }
}

/// <summary>
/// Table <c>OrderingOrders</c> (#398): unique <c>(OrganizationId, BusinessDate, Number)</c> (the day's order numbers),
/// <c>(OrganizationId, SubmissionKey)</c> (a form sent twice) and <c>StatusTokenHash</c> (the status page's token, looked
/// up within the shop of the page's slug). <c>Lines</c> is the priced snapshot as a <c>jsonb</c> array; <c>Version</c> is
/// the concurrency token of the status changes.
/// </summary>
internal sealed class OrderingOrderConfiguration : IEntityTypeConfiguration<OrderingOrder>
{
    /// <summary>The unique index on <c>(OrganizationId, BusinessDate, Number)</c>.</summary>
    public const string NumberIndex = "IX_OrderingOrders_OrganizationId_BusinessDate_Number";

    /// <summary>The unique index on <c>(OrganizationId, SubmissionKey)</c>.</summary>
    public const string SubmissionKeyIndex = "IX_OrderingOrders_OrganizationId_SubmissionKey";

    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);

    private static readonly ValueComparer<IReadOnlyList<OrderingOrderLine>> LinesComparer = new(
        (left, right) => WriteLines(left!) == WriteLines(right!),
        lines => WriteLines(lines).GetHashCode(StringComparison.Ordinal),
        lines => lines);

    public void Configure(EntityTypeBuilder<OrderingOrder> builder)
    {
        builder.ToTable("OrderingOrders", table =>
        {
            table.HasCheckConstraint("CK_OrderingOrders_Lines", "jsonb_typeof(\"Lines\") = 'array'");
            table.HasCheckConstraint("CK_OrderingOrders_Number", "\"Number\" >= 1");
            table.HasCheckConstraint("CK_OrderingOrders_Version", "\"Version\" >= 1");
            table.HasCheckConstraint("CK_OrderingOrders_Total", "\"Subtotal\" >= 0 AND \"Total\" >= \"Subtotal\"");
            table.HasCheckConstraint(
                "CK_OrderingOrders_Asap",
                "(\"ServiceType\" = 'takeout-asap') = (\"AsapMinMinutes\" IS NOT NULL AND \"AsapMaxMinutes\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_OrderingOrders_CancelledAt", "(\"Status\" = 'cancelled') = (\"CancelledAt\" IS NOT NULL)");
        });
        builder.HasKey(order => order.Id);
        builder.Property(order => order.Id).ValueGeneratedNever();
        builder.Property(order => order.ServiceType)
            .HasConversion<WireNameConverter<OrderingServiceType>>()
            .HasMaxLength(24)
            .IsRequired();
        builder.Property(order => order.Status)
            .HasConversion<WireNameConverter<OrderingOrderStatus>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(order => order.Version).IsConcurrencyToken();
        builder.Property(order => order.CustomerName).HasMaxLength(OrderingContact.NameMaxLength).IsRequired();
        builder.Property(order => order.CustomerPhone).HasMaxLength(ReservationContact.PhoneMaxLength).IsRequired();
        builder.Property(order => order.CustomerEmail).HasMaxLength(ReservationContact.EmailMaxLength);
        builder.Property(order => order.OrderComment).HasMaxLength(OrderingContact.OrderCommentMaxLength);
        builder.Property(order => order.Lines)
            .HasConversion(
                new ValueConverter<IReadOnlyList<OrderingOrderLine>, string>(
                    lines => WriteLines(lines),
                    json => ReadLines(json)),
                LinesComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(order => order.StatusTokenHash)
            .HasMaxLength(OrderingOrder.StatusTokenHashLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(order => order.SubmissionKey).HasMaxLength(OrderingOrder.SubmissionKeyLength).IsRequired();
        builder.Ignore(order => order.CanCancel);
        builder.HasIndex(order => new { order.OrganizationId, order.BusinessDate, order.Number }).IsUnique().HasDatabaseName(NumberIndex);
        builder.HasIndex(order => new { order.OrganizationId, order.SubmissionKey }).IsUnique().HasDatabaseName(SubmissionKeyIndex);
        builder.HasIndex(order => order.StatusTokenHash).IsUnique();
        // The slot counts and the pending-per-phone limit.
        builder.HasIndex(order => new { order.OrganizationId, order.PickupAt });
        builder.HasIndex(order => new { order.OrganizationId, order.CustomerPhone, order.Status });
    }

    private static string WriteLines(IReadOnlyList<OrderingOrderLine> lines) => JsonSerializer.Serialize(lines, LineJson);

    private static IReadOnlyList<OrderingOrderLine> ReadLines(string json) =>
        JsonSerializer.Deserialize<List<OrderingOrderLine>>(json, LineJson) ?? [];
}
