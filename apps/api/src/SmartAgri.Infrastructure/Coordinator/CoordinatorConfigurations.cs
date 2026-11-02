using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Coordinator;

/// <summary>
/// Table <c>CoordinatorMembers</c> (#372): LINE users bound to a shop through the 統籌助理. A LINE user
/// is bound to an organization at most once (unique <c>(OrganizationId, LineUserId)</c>); the
/// <c>LineUserId</c> index serves the cross-organization lookup of whose message it is.
/// </summary>
internal sealed class CoordinatorMemberConfiguration : IEntityTypeConfiguration<CoordinatorMember>
{
    public void Configure(EntityTypeBuilder<CoordinatorMember> builder)
    {
        builder.ToTable("CoordinatorMembers");
        builder.HasKey(member => member.Id);
        builder.Property(member => member.Id).ValueGeneratedNever();
        builder.Property(member => member.LineUserId).HasMaxLength(CoordinatorMember.LineUserIdMaxLength).IsRequired();
        builder.Property(member => member.Role)
            .HasConversion<WireNameConverter<CoordinatorRole>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.HasIndex(member => new { member.OrganizationId, member.LineUserId }).IsUnique();
        builder.HasIndex(member => member.LineUserId);
    }
}

/// <summary>
/// Table <c>CoordinatorBindingCodes</c> (#372): single-use binding codes, stored as their SHA-256
/// (unique across all organizations). A used code always records who used it, and only then.
/// </summary>
internal sealed class CoordinatorBindingCodeConfiguration : IEntityTypeConfiguration<CoordinatorBindingCode>
{
    public void Configure(EntityTypeBuilder<CoordinatorBindingCode> builder)
    {
        builder.ToTable("CoordinatorBindingCodes", table =>
        {
            table.HasCheckConstraint(
                "CK_CoordinatorBindingCodes_Used",
                "(\"UsedAt\" IS NULL) = (\"UsedByLineUserId\" IS NULL)");
            table.HasCheckConstraint(
                "CK_CoordinatorBindingCodes_Expiry",
                "\"ExpiresAt\" > \"CreatedAt\"");
        });
        builder.HasKey(code => code.Id);
        builder.Property(code => code.Id).ValueGeneratedNever();
        builder.Property(code => code.CodeHash)
            .HasMaxLength(CoordinatorBindingCode.CodeHashLength)
            .IsRequired();
        builder.Property(code => code.Role)
            .HasConversion<WireNameConverter<CoordinatorRole>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(code => code.UsedByLineUserId).HasMaxLength(CoordinatorMember.LineUserIdMaxLength);
        builder.Property(code => code.IssuedByLineUserId).HasMaxLength(CoordinatorMember.LineUserIdMaxLength);
        builder.HasIndex(code => code.CodeHash).IsUnique();
    }
}

/// <summary>
/// Table <c>CoordinatorShopSettings</c> (#373): one row per organization (unique <c>OrganizationId</c>),
/// read whole. <c>WeeklyHours</c> and <c>Scenarios</c> are <c>jsonb</c> objects in the settings file's own
/// format (<see cref="ShopSettingsJson"/>), never queried by content.
/// </summary>
internal sealed class ShopSettingsConfiguration : IEntityTypeConfiguration<ShopSettings>
{
    private static readonly ValueComparer<WeeklyHours> WeeklyHoursComparer = new(
        (left, right) => ShopSettingsJson.WriteWeeklyHours(left!) == ShopSettingsJson.WriteWeeklyHours(right!),
        hours => ShopSettingsJson.WriteWeeklyHours(hours).GetHashCode(StringComparison.Ordinal),
        hours => ShopSettingsJson.ReadWeeklyHours(ShopSettingsJson.WriteWeeklyHours(hours)));

    private static readonly ValueComparer<IReadOnlyDictionary<ShopScenario, ShopScenarioSettings>> ScenariosComparer = new(
        (left, right) => ShopSettingsJson.WriteScenarios(left!) == ShopSettingsJson.WriteScenarios(right!),
        scenarios => ShopSettingsJson.WriteScenarios(scenarios).GetHashCode(StringComparison.Ordinal),
        scenarios => ShopSettingsJson.ReadScenarios(ShopSettingsJson.WriteScenarios(scenarios)));

    public void Configure(EntityTypeBuilder<ShopSettings> builder)
    {
        builder.ToTable("CoordinatorShopSettings", table =>
        {
            table.HasCheckConstraint("CK_CoordinatorShopSettings_WeeklyHours", "jsonb_typeof(\"WeeklyHours\") = 'object'");
            table.HasCheckConstraint("CK_CoordinatorShopSettings_Scenarios", "jsonb_typeof(\"Scenarios\") = 'object'");
        });
        builder.HasKey(settings => settings.Id);
        builder.Property(settings => settings.Id).ValueGeneratedNever();
        builder.Property(settings => settings.StoreName).HasMaxLength(ShopSettings.StoreNameMaxLength).IsRequired();
        builder.Property(settings => settings.TimeZoneId).HasMaxLength(ShopSettings.TimeZoneIdMaxLength).IsRequired();
        builder.Property(settings => settings.WeeklyHours)
            .HasConversion(
                new ValueConverter<WeeklyHours, string>(
                    hours => ShopSettingsJson.WriteWeeklyHours(hours),
                    json => ShopSettingsJson.ReadWeeklyHours(json)),
                WeeklyHoursComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(settings => settings.Scenarios)
            .HasConversion(
                new ValueConverter<IReadOnlyDictionary<ShopScenario, ShopScenarioSettings>, string>(
                    scenarios => ShopSettingsJson.WriteScenarios(scenarios),
                    json => ShopSettingsJson.ReadScenarios(json)),
                ScenariosComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Ignore(settings => settings.Document);
        builder.HasIndex(settings => settings.OrganizationId).IsUnique();
    }
}
