using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain;
using SmartAgri.Domain.Cases;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Cases;

/// <summary>
/// Table <c>Cases</c> (M7 plan §4; issue #248). Organization filter, <c>Organizations</c> foreign key
/// and the organization concurrency token come from <see cref="AppDbContext"/>'s organization scope.
/// </summary>
/// <remarks>
/// <para>
/// The type, group, creator, owner and previous case are same-organization composite foreign keys with
/// <c>Restrict</c> (decision C: accounts are deactivated, never deleted; groups and types are never
/// deleted; cases are kept forever). The thread, record and issue links are plain columns (decision S).
/// <see cref="Case.EventCount"/> is a concurrency token.
/// </para>
/// <para>
/// Check constraints keep the shape the domain promises: the completion and cancellation times match
/// the status, only a case opened from a database submission has no creator, and each link pair is
/// both set or both empty.
/// </para>
/// </remarks>
internal sealed class CaseConfiguration : IEntityTypeConfiguration<Case>
{
    public void Configure(EntityTypeBuilder<Case> builder)
    {
        var completed = WireNames<CaseStatus>.ToWire(CaseStatus.Completed);
        var cancelled = WireNames<CaseStatus>.ToWire(CaseStatus.Cancelled);
        var fromSubmission = WireNames<CaseOrigin>.ToWire(CaseOrigin.DatabaseSubmission);
        builder.ToTable("Cases", table =>
        {
            table.HasCheckConstraint("CK_Cases_CompletedAt", $"(\"Status\" = '{completed}') = (\"CompletedAt\" IS NOT NULL)");
            table.HasCheckConstraint("CK_Cases_CancelledAt", $"(\"Status\" = '{cancelled}') = (\"CancelledAt\" IS NOT NULL)");
            table.HasCheckConstraint("CK_Cases_Creator", $"(\"Origin\" = '{fromSubmission}') = (\"CreatedByAccountId\" IS NULL)");
            table.HasCheckConstraint("CK_Cases_Thread", "(\"ThreadAssistantId\" IS NULL) = (\"ThreadId\" IS NULL)");
            table.HasCheckConstraint("CK_Cases_Record", "(\"DatabaseId\" IS NULL) = (\"SubmissionId\" IS NULL)");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Status)
            .HasConversion<WireNameConverter<CaseStatus>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(item => item.Origin)
            .HasConversion<WireNameConverter<CaseOrigin>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(item => item.Title).HasMaxLength(Case.TitleMaxLength).IsRequired();
        builder.Property(item => item.Description).HasMaxLength(Case.DescriptionMaxLength).IsRequired();
        builder.Property(item => item.Resolution).HasMaxLength(CaseEvent.NoteMaxLength);
        builder.Property(item => item.CancelReason).HasMaxLength(CaseEvent.NoteMaxLength);
        builder.Property(item => item.EventCount).IsConcurrencyToken();

        // Target of the composite foreign keys from CaseEvents and from a later case (PreviousCaseId).
        builder.HasAlternateKey(item => new { item.Id, item.OrganizationId });

        builder.HasOne<CaseType>()
            .WithMany()
            .HasForeignKey(item => new { item.TypeId, item.OrganizationId })
            .HasPrincipalKey(type => new { type.Id, type.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CaseGroup>()
            .WithMany()
            .HasForeignKey(item => new { item.GroupId, item.OrganizationId })
            .HasPrincipalKey(group => new { group.Id, group.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(item => new { item.CreatedByAccountId, item.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(item => new { item.OwnerAccountId, item.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Case>()
            .WithMany()
            .HasForeignKey(item => new { item.PreviousCaseId, item.OrganizationId })
            .HasPrincipalKey(previous => new { previous.Id, previous.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // The list's default (open cases, newest first) and "is this group still in use" (archiving).
        builder.HasIndex(item => new { item.GroupId, item.Status });
        builder.HasIndex(item => new { item.OrganizationId, item.CreatedAt });
        // The links are read back by id (M7-9's thread, M7-10's record, M7-7's issue).
        builder.HasIndex(item => item.ThreadId);
        builder.HasIndex(item => item.SubmissionId);
        // M7-7: one case per 處理事項, ever (a second 「另開案件」 is 409 with this one's id).
        builder.HasIndex(item => item.AssistantIssueId).IsUnique();
    }
}

/// <summary>
/// Table <c>CaseEvents</c>: the case's history, append-only. <c>(CaseId, Ordinal)</c> is unique, so the
/// history has one stable order. The actor and the recorded owner are composite foreign keys to
/// <c>Accounts</c> of the same organization (<c>Restrict</c>); the groups are composite foreign keys to
/// <c>CaseGroups</c>.
/// </summary>
internal sealed class CaseEventConfiguration : IEntityTypeConfiguration<CaseEvent>
{
    public void Configure(EntityTypeBuilder<CaseEvent> builder)
    {
        builder.ToTable("CaseEvents");
        builder.HasKey(caseEvent => caseEvent.Id);
        builder.Property(caseEvent => caseEvent.Id).ValueGeneratedNever();
        builder.Property(caseEvent => caseEvent.Action)
            .HasConversion<WireNameConverter<CaseEventAction>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(caseEvent => caseEvent.Note).HasMaxLength(CaseEvent.NoteMaxLength);
        builder.Property(caseEvent => caseEvent.Status)
            .HasConversion(
                new ValueConverter<CaseStatus?, string?>(
                    status => status.HasValue ? WireNames<CaseStatus>.ToWire(status.Value) : null,
                    name => name == null ? (CaseStatus?)null : WireNames<CaseStatus>.Parse(name)))
            .HasMaxLength(16);

        builder.HasIndex(caseEvent => new { caseEvent.CaseId, caseEvent.Ordinal }).IsUnique();

        builder.HasOne<Case>()
            .WithMany()
            .HasForeignKey(caseEvent => new { caseEvent.CaseId, caseEvent.OrganizationId })
            .HasPrincipalKey(item => new { item.Id, item.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(caseEvent => new { caseEvent.ActorAccountId, caseEvent.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Also "has this account ever been the case owner" (CaseVisibility).
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(caseEvent => new { caseEvent.OwnerAccountId, caseEvent.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CaseGroup>()
            .WithMany()
            .HasForeignKey(caseEvent => new { caseEvent.FromGroupId, caseEvent.OrganizationId })
            .HasPrincipalKey(group => new { group.Id, group.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CaseGroup>()
            .WithMany()
            .HasForeignKey(caseEvent => new { caseEvent.ToGroupId, caseEvent.OrganizationId })
            .HasPrincipalKey(group => new { group.Id, group.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
