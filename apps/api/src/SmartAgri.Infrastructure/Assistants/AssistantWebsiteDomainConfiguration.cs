using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantWebsiteDomains</c> (M5a plan §4): <c>(AssistantId, Domain)</c> is the key, so a
/// domain is listed once per assistant. The foreign key to the channel includes
/// <c>OrganizationId</c> and cascades. The limit of
/// <see cref="AssistantWebsiteChannel.MaxAllowedDomains"/> per assistant is the Application rule's
/// (checked before any write), not a constraint.
/// </summary>
internal sealed class AssistantWebsiteDomainConfiguration : IEntityTypeConfiguration<AssistantWebsiteDomain>
{
    public void Configure(EntityTypeBuilder<AssistantWebsiteDomain> builder)
    {
        builder.ToTable("AssistantWebsiteDomains", table => table.HasCheckConstraint(
            "CK_AssistantWebsiteDomains_Domain",
            "\"Domain\" = lower(\"Domain\") AND length(\"Domain\") > 0"));
        builder.HasKey(domain => new { domain.AssistantId, domain.Domain });
        builder.Property(domain => domain.Domain)
            .HasMaxLength(AssistantWebsiteDomain.DomainMaxLength)
            .IsRequired();

        builder.HasOne<AssistantWebsiteChannel>()
            .WithMany()
            .HasForeignKey(domain => new { domain.AssistantId, domain.OrganizationId })
            .HasPrincipalKey(channel => new { channel.AssistantId, channel.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
