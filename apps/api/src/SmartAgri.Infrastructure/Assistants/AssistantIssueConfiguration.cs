using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Cases;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantIssues</c> (M3.5 plan §4, issue #126). Cascades with its assistant, like
/// the test set. The assignee and reporter are composite foreign keys to <c>Accounts</c>, so the
/// database refuses an account of another organization (<c>Restrict</c>: accounts are never
/// deleted while referenced). <see cref="AssistantIssue.TestRunId"/> and
/// <see cref="AssistantIssue.TestResultId"/> are deliberately not foreign keys (runs are
/// pruned; the issue keeps its own snapshot). <see cref="AssistantIssue.EventCount"/> is a
/// concurrency token.
/// </summary>
internal sealed class AssistantIssueConfiguration : IEntityTypeConfiguration<AssistantIssue>
{
    public void Configure(EntityTypeBuilder<AssistantIssue> builder)
    {
        var resolved = WireNames<AssistantIssueStatus>.ToWire(AssistantIssueStatus.Resolved);
        var notAssistantIssue = WireNames<AssistantIssueResolutionKind>.ToWire(AssistantIssueResolutionKind.NotAssistantIssue);
        builder.ToTable("AssistantIssues", table =>
        {
            table.HasCheckConstraint(
                "CK_AssistantIssues_ResolvedAt",
                $"(\"Status\" = '{resolved}') = (\"ResolvedAt\" IS NOT NULL)");

            // M7-7: a resolved issue always says how; an unresolved one never does.
            table.HasCheckConstraint(
                "CK_AssistantIssues_ResolutionKind",
                $"(\"Status\" = '{resolved}') = (\"ResolutionKind\" IS NOT NULL)");

            // Only a 「非助理問題」 resolution links a case, and it always does (COALESCE: a NULL
            // comparison would pass any CHECK).
            table.HasCheckConstraint(
                "CK_AssistantIssues_LinkedCaseId",
                $"COALESCE(\"ResolutionKind\" = '{notAssistantIssue}', FALSE) = (\"LinkedCaseId\" IS NOT NULL)");
        });
        builder.HasKey(issue => issue.Id);
        builder.Property(issue => issue.Id).ValueGeneratedNever();
        builder.Property(issue => issue.Source)
            .HasConversion<WireNameConverter<AssistantIssueSource>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(issue => issue.Status)
            .HasConversion<WireNameConverter<AssistantIssueStatus>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(issue => issue.Title).HasMaxLength(AssistantIssue.TitleMaxLength).IsRequired();
        builder.Property(issue => issue.TestFailureReason)
            .HasConversion(
                new ValueConverter<AssistantTestFailureReason?, string?>(
                    reason => reason.HasValue ? WireNames<AssistantTestFailureReason>.ToWire(reason.Value) : null,
                    name => name == null ? (AssistantTestFailureReason?)null : WireNames<AssistantTestFailureReason>.Parse(name)))
            .HasMaxLength(32);
        builder.Property(issue => issue.QuestionSnapshot).HasMaxLength(AssistantTestCase.QuestionMaxLength);
        builder.Property(issue => issue.ResolutionNote).HasMaxLength(AssistantIssueEvent.NoteMaxLength);
        builder.Property(issue => issue.ResolutionKind)
            .HasConversion(
                new ValueConverter<AssistantIssueResolutionKind?, string?>(
                    kind => kind.HasValue ? WireNames<AssistantIssueResolutionKind>.ToWire(kind.Value) : null,
                    name => name == null ? (AssistantIssueResolutionKind?)null : WireNames<AssistantIssueResolutionKind>.Parse(name)))
            .HasMaxLength(32);
        builder.Property(issue => issue.EventCount).IsConcurrencyToken();

        // Target of the composite foreign key from AssistantIssueEvents.
        builder.HasAlternateKey(issue => new { issue.Id, issue.OrganizationId });

        builder.HasIndex(issue => new { issue.AssistantId, issue.CreatedAt });
        builder.HasIndex(issue => new { issue.AssigneeAccountId, issue.Status });
        builder.HasIndex(issue => new { issue.ReporterAccountId, issue.Source });
        builder.HasIndex(issue => issue.TestResultId);

        builder.HasOne<Assistant>()
            .WithMany()
            .HasForeignKey(issue => new { issue.AssistantId, issue.OrganizationId })
            .HasPrincipalKey(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(issue => new { issue.AssigneeAccountId, issue.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(issue => new { issue.ReporterAccountId, issue.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // M7-7: the case opened from the issue — a same-organization composite foreign key (cases are
        // never deleted). The case's own link back is a plain id (the issue goes with its assistant).
        builder.HasOne<Case>()
            .WithMany()
            .HasForeignKey(issue => new { issue.LinkedCaseId, issue.OrganizationId })
            .HasPrincipalKey(item => new { item.Id, item.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
