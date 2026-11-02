using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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
