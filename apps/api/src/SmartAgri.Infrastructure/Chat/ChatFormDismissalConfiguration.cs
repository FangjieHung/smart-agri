using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Chat;

namespace SmartAgri.Infrastructure.Chat;

/// <summary>
/// Table <c>ChatFormDismissals</c> (M4 #171): closed in-conversation forms, for sampling mis-triggered
/// form requests. No column holds content; no foreign keys (an operational log, like
/// <c>AnswerOutcomes</c>, that outlives a deleted assistant or database).
/// </summary>
internal sealed class ChatFormDismissalConfiguration : IEntityTypeConfiguration<ChatFormDismissal>
{
    public void Configure(EntityTypeBuilder<ChatFormDismissal> builder)
    {
        builder.ToTable("ChatFormDismissals");
        builder.HasKey(dismissal => dismissal.Id);
        builder.Property(dismissal => dismissal.Id).ValueGeneratedNever();

        // An organization's dismissals over time, by assistant (the sampling query).
        builder.HasIndex(dismissal => new { dismissal.OrganizationId, dismissal.AssistantId, dismissal.At });
    }
}
