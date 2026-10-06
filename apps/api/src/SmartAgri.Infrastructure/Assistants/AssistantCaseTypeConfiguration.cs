using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantCaseTypes</c> (M7 plan §4; #254). Both foreign keys include <c>OrganizationId</c>,
/// so an assistant can only ever propose a type of its own organization. The row goes with the
/// assistant (cascade); case types are never deleted, only deactivated, so that side is <c>Restrict</c>
/// (the type's foreign-key index also serves "which assistants propose this type").
/// </summary>
internal sealed class AssistantCaseTypeConfiguration : IEntityTypeConfiguration<AssistantCaseType>
{
    public void Configure(EntityTypeBuilder<AssistantCaseType> builder)
    {
        builder.ToTable("AssistantCaseTypes");
        builder.HasKey(link => new { link.AssistantId, link.CaseTypeId });

        builder.HasOne<Assistant>()
            .WithMany()
            .HasForeignKey(link => new { link.AssistantId, link.OrganizationId })
            .HasPrincipalKey(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<CaseType>()
            .WithMany()
            .HasForeignKey(link => new { link.CaseTypeId, link.OrganizationId })
            .HasPrincipalKey(type => new { type.Id, type.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
