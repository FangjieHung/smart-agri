using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Notifications;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Notifications;

/// <summary>
/// Table <c>VisitorHandoffs</c> (#355): 轉真人 notifications — the assistant, the channel and times, no
/// content. Cascades with its assistant (the notification is about that assistant's visitors). The
/// account that marked it handled is a same-organization composite foreign key (<c>Restrict</c>:
/// accounts are never deleted while referenced).
/// </summary>
internal sealed class VisitorHandoffConfiguration : IEntityTypeConfiguration<VisitorHandoff>
{
    public void Configure(EntityTypeBuilder<VisitorHandoff> builder)
    {
        builder.ToTable("VisitorHandoffs", table =>
            table.HasCheckConstraint(
                "CK_VisitorHandoffs_Handled",
                "(\"HandledAt\" IS NULL) = (\"HandledByAccountId\" IS NULL)"));
        builder.HasKey(handoff => handoff.Id);
        builder.Property(handoff => handoff.Id).ValueGeneratedNever();
        builder.Property(handoff => handoff.Channel)
            .HasConversion<WireNameConverter<VisitorHandoffChannel>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Ignore(handoff => handoff.IsPending);

        // The owner's pending list and count, newest first.
        builder.HasIndex(handoff => new { handoff.AssistantId, handoff.HandledAt, handoff.CreatedAt });

        builder.HasOne<Assistant>()
            .WithMany()
            .HasForeignKey(handoff => new { handoff.AssistantId, handoff.OrganizationId })
            .HasPrincipalKey(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(handoff => new { handoff.HandledByAccountId, handoff.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
