using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantWebsiteChannels</c> (M5a plan §4): keyed by the assistant, at most one row
/// each. The foreign key includes <c>OrganizationId</c>, so the row can only belong to an assistant
/// of its own organization; it cascades, so deleting the assistant removes the channel and its
/// domains. <see cref="AssistantWebsiteChannel.PublishedByAccountId"/> has no foreign key: only the
/// owner may publish, and an account that owns assistants cannot be deleted (Restrict on
/// <c>Assistants</c>).
/// </summary>
internal sealed class AssistantWebsiteChannelConfiguration : IEntityTypeConfiguration<AssistantWebsiteChannel>
{
    public void Configure(EntityTypeBuilder<AssistantWebsiteChannel> builder)
    {
        builder.ToTable("AssistantWebsiteChannels", table =>
        {
            table.HasCheckConstraint("CK_AssistantWebsiteChannels_Revision", "\"Revision\" >= 1");
            // A draft has no publication; a published or paused channel always has one.
            table.HasCheckConstraint(
                "CK_AssistantWebsiteChannels_Published",
                "(\"State\" = 'draft') = (\"PublishedAt\" IS NULL AND \"PublishedByAccountId\" IS NULL)");
        });
        builder.HasKey(channel => channel.AssistantId);
        builder.Property(channel => channel.AssistantId).ValueGeneratedNever();
        builder.Property(channel => channel.DisplayName)
            .HasMaxLength(AssistantWebsiteChannel.DisplayNameMaxLength)
            .IsRequired();
        builder.Property(channel => channel.WelcomeMessage)
            .HasMaxLength(AssistantWebsiteChannel.WelcomeMessageMaxLength)
            .IsRequired();
        builder.Property(channel => channel.BrandColor)
            .HasConversion<WireNameConverter<WebsiteBrandColor>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(channel => channel.Position)
            .HasConversion<WireNameConverter<WebsiteLauncherPosition>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(channel => channel.State)
            .HasConversion<WireNameConverter<WebsiteChannelState>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(channel => channel.Revision).IsRequired();

        // Target of the domains' composite foreign key.
        builder.HasAlternateKey(channel => new { channel.AssistantId, channel.OrganizationId });

        builder.HasOne<Assistant>()
            .WithOne()
            .HasForeignKey<AssistantWebsiteChannel>(channel => new { channel.AssistantId, channel.OrganizationId })
            .HasPrincipalKey<Assistant>(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
