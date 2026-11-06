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

/// <summary>
/// Table <c>CoordinatorCommands</c> (#374): one row per 統籌助理 command (decision F; Slice 4 adds its
/// results). Check constraints keep a row consistent with its status and scenario (a previewed or confirmed
/// command has its date, and its time exactly for 提早打烊／延後開店; a cancelled one may have been dropped while
/// still waiting for them); at most one confirmed
/// reopen per command (unique, filtered). Indexes serve a sender's open commands and a shop's reopen
/// candidates. The reopened command is a foreign key within the same organization.
/// </summary>
internal sealed class CoordinatorCommandConfiguration : IEntityTypeConfiguration<CoordinatorCommand>
{
    public void Configure(EntityTypeBuilder<CoordinatorCommand> builder)
    {
        builder.ToTable("CoordinatorCommands", table =>
        {
            table.HasCheckConstraint(
                "CK_CoordinatorCommands_Confirmed",
                "(\"Status\" = 'confirmed') = (\"ConfirmedAt\" IS NOT NULL) AND (\"ConfirmedAt\" IS NULL) = (\"ConfirmedByLineUserId\" IS NULL)");
            table.HasCheckConstraint(
                "CK_CoordinatorCommands_Details",
                "\"Status\" IN ('awaiting-details', 'cancelled') OR (\"Date\" IS NOT NULL AND (\"Time\" IS NOT NULL) = (\"Scenario\" IN ('close-early', 'open-late')))");
            table.HasCheckConstraint(
                "CK_CoordinatorCommands_Reopen",
                "(\"Scenario\" = 'reopen') = (\"ReopensCommandId\" IS NOT NULL) AND (\"Scenario\" = 'reopen' OR NOT \"CorrectionBroadcast\")");
            table.HasCheckConstraint("CK_CoordinatorCommands_Version", "\"Version\" >= 1");
            table.HasCheckConstraint("CK_CoordinatorCommands_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
        });
        builder.HasKey(command => command.Id);
        builder.Property(command => command.Id).ValueGeneratedNever();
        builder.Property(command => command.RequestedByLineUserId).HasMaxLength(CoordinatorMember.LineUserIdMaxLength).IsRequired();
        builder.Property(command => command.ConfirmedByLineUserId).HasMaxLength(CoordinatorMember.LineUserIdMaxLength);
        builder.Property(command => command.Scenario)
            .HasConversion<WireNameConverter<ShopScenario>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(command => command.Status)
            .HasConversion<WireNameConverter<CoordinatorCommandStatus>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(command => command.AnnouncementText).HasMaxLength(CoordinatorCommand.AnnouncementMaxLength);
        builder.HasAlternateKey(command => new { command.Id, command.OrganizationId });
        builder.HasOne<CoordinatorCommand>()
            .WithMany()
            .HasForeignKey(command => new { command.ReopensCommandId, command.OrganizationId })
            .HasPrincipalKey(command => new { command.Id, command.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(command => new { command.OrganizationId, command.RequestedByLineUserId, command.Status });
        builder.HasIndex(command => new { command.OrganizationId, command.Status, command.Date });
        builder.HasIndex(command => command.ReopensCommandId)
            .IsUnique()
            .HasFilter("\"Status\" = 'confirmed'")
            .HasDatabaseName("IX_CoordinatorCommands_ReopensCommandId");
    }
}

/// <summary>
/// Table <c>CoordinatorCommandActions</c> (#375): one row per action of a confirmed command (decision F). Check
/// constraints keep a row consistent with its kind, step and status (an ordering system pauses and resumes, is
/// named and always a reminder; an undo names what it undoes; 〔已完成〕 records who; a failure has its code);
/// positions are unique per command. The command and the undone row are foreign keys within the same organization.
/// Indexes serve a command's rows.
/// </summary>
internal sealed class CoordinatorCommandActionConfiguration : IEntityTypeConfiguration<CoordinatorCommandAction>
{
    public void Configure(EntityTypeBuilder<CoordinatorCommandAction> builder)
    {
        builder.ToTable("CoordinatorCommandActions", table =>
        {
            table.HasCheckConstraint(
                "CK_CoordinatorCommandActions_Ordering",
                "(\"Kind\" = 'ordering') = (\"Step\" IN ('pause', 'resume')) AND (\"Kind\" = 'ordering') = (\"Name\" IS NOT NULL) "
                + "AND (\"Kind\" <> 'ordering' OR \"Mode\" = 'reminder')");
            table.HasCheckConstraint(
                "CK_CoordinatorCommandActions_Undo",
                "(\"Step\" <> 'undo' OR \"UndoesActionId\" IS NOT NULL) AND (\"Step\" NOT IN ('apply', 'pause') OR \"UndoesActionId\" IS NULL)");
            table.HasCheckConstraint(
                "CK_CoordinatorCommandActions_Done",
                "(\"Status\" = 'done-manually') = (\"CompletedByLineUserId\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CoordinatorCommandActions_Completed",
                "\"Status\" NOT IN ('succeeded', 'failed', 'done-manually') OR \"CompletedAt\" IS NOT NULL");
            table.HasCheckConstraint(
                "CK_CoordinatorCommandActions_Failure",
                "\"Status\" <> 'failed' OR \"FailureCode\" IS NOT NULL");
            table.HasCheckConstraint("CK_CoordinatorCommandActions_Position", "\"Position\" >= 0 AND \"Attempts\" >= 0");
        });
        builder.HasKey(action => action.Id);
        builder.Property(action => action.Id).ValueGeneratedNever();
        builder.Property(action => action.Kind)
            .HasConversion<WireNameConverter<ShopActionKind>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(action => action.Name).HasMaxLength(ShopSettingsJson.OrderingNameMaxLength);
        builder.Property(action => action.Step)
            .HasConversion<WireNameConverter<CoordinatorActionStep>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(action => action.Mode)
            .HasConversion<WireNameConverter<ShopActionMode>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(action => action.Status)
            .HasConversion<WireNameConverter<CoordinatorActionStatus>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(action => action.StepsText).HasMaxLength(CoordinatorCommandAction.StepsTextMaxLength).IsRequired();
        builder.Property(action => action.ImageUrl).HasMaxLength(ShopSettingsJson.ImageUrlMaxLength);
        builder.Property(action => action.RecipientLineUserId).HasMaxLength(CoordinatorMember.LineUserIdMaxLength).IsRequired();
        builder.Property(action => action.CompletedByLineUserId).HasMaxLength(CoordinatorMember.LineUserIdMaxLength);
        builder.Property(action => action.FailureCode).HasMaxLength(CoordinatorCommandAction.FailureCodeMaxLength);
        builder.Property(action => action.ExternalRef).HasMaxLength(CoordinatorCommandAction.ExternalRefMaxLength);
        builder.Ignore(action => action.IsOrdering);
        builder.HasAlternateKey(action => new { action.Id, action.OrganizationId });
        builder.HasOne<CoordinatorCommand>()
            .WithMany()
            .HasForeignKey(action => new { action.CommandId, action.OrganizationId })
            .HasPrincipalKey(command => new { command.Id, command.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CoordinatorCommandAction>()
            .WithMany()
            .HasForeignKey(action => new { action.UndoesActionId, action.OrganizationId })
            .HasPrincipalKey(action => new { action.Id, action.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(action => new { action.CommandId, action.Position }).IsUnique();
    }
}

/// <summary>
/// Table <c>CoordinatorConnections</c> (#376): a shop's credential per external service, at most one per organization
/// and service (unique <c>(OrganizationId, Service)</c>). The credential is a <see cref="SmartAgri.Domain.Secrets.ProtectedSecret"/>
/// complex property of three columns (<c>Secret_Ciphertext</c>, <c>Secret_LastFour</c>, <c>Secret_SetAt</c>), as on
/// <c>AssistantLineChannels</c>.
/// </summary>
internal sealed class CoordinatorConnectionConfiguration : IEntityTypeConfiguration<CoordinatorConnection>
{
    public void Configure(EntityTypeBuilder<CoordinatorConnection> builder)
    {
        builder.ToTable("CoordinatorConnections");
        builder.HasKey(connection => connection.Id);
        builder.Property(connection => connection.Id).ValueGeneratedNever();
        builder.Property(connection => connection.Service)
            .HasConversion<WireNameConverter<CoordinatorService>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.ComplexProperty(connection => connection.Secret, secret =>
        {
            secret.IsRequired();
            secret.Property(value => value.Ciphertext).IsRequired();
            secret.Property(value => value.LastFour).HasMaxLength(16);
            secret.Property(value => value.SetAt).IsRequired();
        });
        builder.Property(connection => connection.ExternalId).HasMaxLength(CoordinatorConnection.ExternalIdMaxLength).IsRequired();
        builder.Property(connection => connection.DisplayName).HasMaxLength(CoordinatorConnection.DisplayNameMaxLength).IsRequired();
        builder.Property(connection => connection.Status)
            .HasConversion<WireNameConverter<CoordinatorConnectionStatus>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.HasIndex(connection => new { connection.OrganizationId, connection.Service }).IsUnique();
    }
}
