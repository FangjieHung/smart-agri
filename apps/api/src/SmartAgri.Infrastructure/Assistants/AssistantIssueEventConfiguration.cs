using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantIssueEvents</c> (M3.5 plan §4, issue #126): the issue's history, cascading
/// with it. <c>(IssueId, Ordinal)</c> is unique, so the history has one stable order. The actor
/// (and a recorded assignee) are composite foreign keys to <c>Accounts</c> of the same
/// organization.
/// </summary>
internal sealed class AssistantIssueEventConfiguration : IEntityTypeConfiguration<AssistantIssueEvent>
{
    public void Configure(EntityTypeBuilder<AssistantIssueEvent> builder)
    {
        builder.ToTable("AssistantIssueEvents");
        builder.HasKey(issueEvent => issueEvent.Id);
        builder.Property(issueEvent => issueEvent.Id).ValueGeneratedNever();
        builder.Property(issueEvent => issueEvent.Action)
            .HasConversion<WireNameConverter<AssistantIssueEventAction>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(issueEvent => issueEvent.Note).HasMaxLength(AssistantIssueEvent.NoteMaxLength);
        builder.Property(issueEvent => issueEvent.Status)
            .HasConversion(
                new ValueConverter<AssistantIssueStatus?, string?>(
                    status => status.HasValue ? WireNames<AssistantIssueStatus>.ToWire(status.Value) : null,
                    name => name == null ? (AssistantIssueStatus?)null : WireNames<AssistantIssueStatus>.Parse(name)))
            .HasMaxLength(16);

        builder.HasIndex(issueEvent => new { issueEvent.IssueId, issueEvent.Ordinal }).IsUnique();

        builder.HasOne<AssistantIssue>()
            .WithMany()
            .HasForeignKey(issueEvent => new { issueEvent.IssueId, issueEvent.OrganizationId })
            .HasPrincipalKey(issue => new { issue.Id, issue.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(issueEvent => new { issueEvent.ActorAccountId, issueEvent.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(issueEvent => new { issueEvent.AssigneeAccountId, issueEvent.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
