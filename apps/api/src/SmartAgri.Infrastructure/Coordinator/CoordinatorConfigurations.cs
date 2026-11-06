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
/// read whole. <c>WeeklyHours</c>, <c>Scenarios</c> and <c>Reservations</c> (#389, nullable) are <c>jsonb</c> objects in
/// the settings file's own format (<see cref="ShopSettingsJson"/>), never queried by content; the reservation page
/// finds its shop by <c>ReservationSlug</c>, unique across all organizations.
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

    // A record: equal by value, and immutable, so the snapshot can be the same instance.
    private static readonly ValueComparer<ShopReservationSettings?> ReservationsComparer = new(
        (left, right) => Equals(left, right),
        reservations => reservations == null ? 0 : reservations.GetHashCode(),
        reservations => reservations);

    /// <summary>The unique index on <c>ReservationSlug</c>: an import that loses the race for a slug fails on it.</summary>
    public const string ReservationSlugIndex = "IX_CoordinatorShopSettings_ReservationSlug";

    public void Configure(EntityTypeBuilder<ShopSettings> builder)
    {
        builder.ToTable("CoordinatorShopSettings", table =>
        {
            table.HasCheckConstraint("CK_CoordinatorShopSettings_WeeklyHours", "jsonb_typeof(\"WeeklyHours\") = 'object'");
            table.HasCheckConstraint("CK_CoordinatorShopSettings_Scenarios", "jsonb_typeof(\"Scenarios\") = 'object'");
            table.HasCheckConstraint(
                "CK_CoordinatorShopSettings_Reservations",
                "\"Reservations\" IS NULL OR jsonb_typeof(\"Reservations\") = 'object'");
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
        builder.Property(settings => settings.Reservations)
            .HasConversion(
                new ValueConverter<ShopReservationSettings?, string?>(
                    reservations => reservations == null ? null : ShopSettingsJson.WriteReservations(reservations),
                    json => json == null ? null : ShopSettingsJson.ReadReservations(json)),
                ReservationsComparer)
            .HasColumnType("jsonb");
        builder.Property(settings => settings.ReservationSlug).HasMaxLength(ShopReservationSettings.SlugMaxLength);
        builder.Ignore(settings => settings.Document);
        builder.HasIndex(settings => settings.OrganizationId).IsUnique();
        builder.HasIndex(settings => settings.ReservationSlug)
            .IsUnique()
            .HasFilter("\"ReservationSlug\" IS NOT NULL")
            .HasDatabaseName(ReservationSlugIndex);
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
/// <c>AssistantLineChannels</c>. <c>ReauthorizationRequestedAt</c> (#377) is set only while revoked.
/// </summary>
internal sealed class CoordinatorConnectionConfiguration : IEntityTypeConfiguration<CoordinatorConnection>
{
    public void Configure(EntityTypeBuilder<CoordinatorConnection> builder)
    {
        builder.ToTable("CoordinatorConnections", table =>
            table.HasCheckConstraint(
                "CK_CoordinatorConnections_Reauthorization",
                "\"ReauthorizationRequestedAt\" IS NULL OR \"Status\" = 'revoked'"));
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

/// <summary>
/// Table <c>CoordinatorAuthorizationLinks</c> (#377): single-use FB and Google authorization links, stored as their
/// token's SHA-256 (unique across all organizations, for the anonymous lookup). Check constraints keep the service to
/// FB and Google and the expiry after the creation.
/// </summary>
internal sealed class CoordinatorAuthorizationLinkConfiguration : IEntityTypeConfiguration<CoordinatorAuthorizationLink>
{
    public void Configure(EntityTypeBuilder<CoordinatorAuthorizationLink> builder)
    {
        builder.ToTable("CoordinatorAuthorizationLinks", table =>
        {
            table.HasCheckConstraint("CK_CoordinatorAuthorizationLinks_Service", "\"Service\" IN ('facebook', 'google')");
            table.HasCheckConstraint("CK_CoordinatorAuthorizationLinks_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
        });
        builder.HasKey(link => link.Id);
        builder.Property(link => link.Id).ValueGeneratedNever();
        builder.Property(link => link.Service)
            .HasConversion<WireNameConverter<CoordinatorService>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(link => link.Purpose)
            .HasConversion<WireNameConverter<CoordinatorAuthorizationPurpose>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(link => link.TokenHash).HasMaxLength(CoordinatorAuthorizationLink.TokenHashLength).IsRequired();
        builder.HasIndex(link => link.TokenHash).IsUnique();
        builder.HasIndex(link => new { link.OrganizationId, link.Service });
    }
}

/// <summary>
/// Table <c>CoordinatorReservations</c> (#389): one row per reservation a customer sent (store-reservations design
/// §3.1 point 6). Check constraints keep the status to its five values, a member's accept or decline with its time, a
/// cancellation with its reason (a 店休 scenario) and command, and the time of the last status change exactly when
/// it is no longer pending; the submission key is unique across all organizations (a form sent twice is one row).
/// Indexes serve the pending count per phone, a day's reservations (Slice R4) and the list and retention by time.
/// The cancelling command is a foreign key within the same organization; the member who handled it is not (members
/// can leave, the history stays).
/// </summary>
internal sealed class CoordinatorReservationConfiguration : IEntityTypeConfiguration<CoordinatorReservation>
{
    /// <summary>The unique index on <c>SubmissionKey</c>.</summary>
    public const string SubmissionKeyIndex = "IX_CoordinatorReservations_SubmissionKey";

    public void Configure(EntityTypeBuilder<CoordinatorReservation> builder)
    {
        builder.ToTable("CoordinatorReservations", table =>
        {
            table.HasCheckConstraint(
                "CK_CoordinatorReservations_Status",
                "\"Status\" IN ('pending', 'accepted', 'declined', 'cancelled', 'expired')");
            table.HasCheckConstraint(
                "CK_CoordinatorReservations_MembersNotified",
                "\"MembersNotified\" IN ('pending', 'sent', 'not-sent')");
            table.HasCheckConstraint(
                "CK_CoordinatorReservations_Handled",
                "(\"HandledByMemberId\" IS NULL) = (\"HandledAt\" IS NULL) AND (\"Status\" NOT IN ('accepted', 'declined') OR \"HandledAt\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CoordinatorReservations_Cancelled",
                "(\"Status\" = 'cancelled') = (\"CancelReason\" IS NOT NULL) AND (\"CancelledByCommandId\" IS NULL OR \"Status\" = 'cancelled') "
                + "AND (\"CancelReason\" IS NULL OR \"CancelReason\" IN ('temporary-closure', 'close-early', 'open-late'))");
            table.HasCheckConstraint(
                "CK_CoordinatorReservations_StatusChanged",
                "(\"Status\" = 'pending') = (\"StatusChangedAt\" IS NULL)");
            table.HasCheckConstraint("CK_CoordinatorReservations_PartySize", "\"PartySize\" >= 1");
            table.HasCheckConstraint("CK_CoordinatorReservations_Version", "\"Version\" >= 1");
        });
        builder.HasKey(reservation => reservation.Id);
        builder.Property(reservation => reservation.Id).ValueGeneratedNever();
        builder.Property(reservation => reservation.CustomerName).HasMaxLength(ReservationContact.NameMaxLength).IsRequired();
        builder.Property(reservation => reservation.CustomerPhone).HasMaxLength(ReservationContact.PhoneMaxLength).IsRequired();
        builder.Property(reservation => reservation.CustomerEmail).HasMaxLength(ReservationContact.EmailMaxLength);
        builder.Property(reservation => reservation.Note).HasMaxLength(ReservationContact.NoteMaxLength);
        builder.Property(reservation => reservation.Status)
            .HasConversion<WireNameConverter<CoordinatorReservationStatus>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(reservation => reservation.SubmissionKey).HasMaxLength(CoordinatorReservation.SubmissionKeyLength).IsRequired();
        builder.Property(reservation => reservation.CancelReason)
            .HasConversion<WireNameConverter<ShopScenario>>()
            .HasMaxLength(32);
        builder.Property(reservation => reservation.MembersNotified)
            .HasConversion<WireNameConverter<CoordinatorReservationNotice>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.HasOne<CoordinatorCommand>()
            .WithMany()
            .HasForeignKey(reservation => new { reservation.CancelledByCommandId, reservation.OrganizationId })
            .HasPrincipalKey(command => new { command.Id, command.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(reservation => reservation.SubmissionKey).IsUnique().HasDatabaseName(SubmissionKeyIndex);
        builder.HasIndex(reservation => new { reservation.OrganizationId, reservation.CustomerPhone, reservation.Status });
        builder.HasIndex(reservation => new { reservation.OrganizationId, reservation.Date });
        builder.HasIndex(reservation => new { reservation.OrganizationId, reservation.ReservedAt });
    }
}

/// <summary>
/// Table <c>CoordinatorPushLogs</c> (#390): one row per push of the 統籌助理 that LINE accepted — the shop, when, the
/// purpose (check constraint on its four wire names) and the recipient count (at least 1). Indexed by organization and
/// time for a month's counts and the retention sweep.
/// </summary>
internal sealed class CoordinatorPushLogConfiguration : IEntityTypeConfiguration<CoordinatorPushLog>
{
    public void Configure(EntityTypeBuilder<CoordinatorPushLog> builder)
    {
        builder.ToTable("CoordinatorPushLogs", table =>
        {
            table.HasCheckConstraint(
                "CK_CoordinatorPushLogs_Purpose",
                "\"Purpose\" IN ('reservation', 'reminder', 'follow-up', 're-authorization')");
            table.HasCheckConstraint("CK_CoordinatorPushLogs_Recipients", "\"Recipients\" >= 1");
        });
        builder.HasKey(log => log.Id);
        builder.Property(log => log.Id).ValueGeneratedNever();
        builder.Property(log => log.Purpose)
            .HasConversion<WireNameConverter<CoordinatorPushPurpose>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.HasIndex(log => new { log.OrganizationId, log.SentAt });
    }
}
