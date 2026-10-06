using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Cases;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Infrastructure.Cases;

/// <summary>Index names the endpoints recognize a violation of.</summary>
public static class CaseGroupIndexes
{
    /// <summary>The unique index on <c>(OrganizationId, Name)</c>: a second group with the same name
    /// in the organization, even from a concurrent request, fails on it.</summary>
    public const string Name = "IX_CaseGroups_OrganizationId_Name";
}

/// <summary>
/// Table <c>CaseGroups</c> (M7 plan §4; issue #246). Organization filter, <c>Organizations</c>
/// foreign key and concurrency token come from <see cref="AppDbContext"/>'s organization scope.
/// </summary>
internal sealed class CaseGroupConfiguration : IEntityTypeConfiguration<CaseGroup>
{
    public void Configure(EntityTypeBuilder<CaseGroup> builder)
    {
        builder.ToTable("CaseGroups");
        builder.HasKey(group => group.Id);
        builder.Property(group => group.Id).ValueGeneratedNever();
        builder.Property(group => group.Name).HasMaxLength(CaseGroup.NameMaxLength).IsRequired();
        builder.Ignore(group => group.IsArchived);

        // Target of the composite foreign keys from the members and their history (and later the
        // case types' default group and the cases, M7-2/M7-3), so the database refuses a row whose
        // organization differs from its group's.
        builder.HasAlternateKey(group => new { group.Id, group.OrganizationId });

        builder.HasIndex(group => new { group.OrganizationId, group.Name }).IsUnique().HasDatabaseName(CaseGroupIndexes.Name);
    }
}

/// <summary>
/// Table <c>CaseGroupMembers</c>. Both foreign keys include <c>OrganizationId</c>. The group's
/// cascades; the account's is <c>Restrict</c> (decision C: an account that is still a member cannot
/// be deleted — accounts are deactivated, not deleted).
/// </summary>
internal sealed class CaseGroupMemberConfiguration : IEntityTypeConfiguration<CaseGroupMember>
{
    public void Configure(EntityTypeBuilder<CaseGroupMember> builder)
    {
        builder.ToTable("CaseGroupMembers");
        builder.HasKey(member => new { member.GroupId, member.AccountId });

        builder.HasOne<CaseGroup>()
            .WithMany()
            .HasForeignKey(member => new { member.GroupId, member.OrganizationId })
            .HasPrincipalKey(group => new { group.Id, group.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(member => new { member.AccountId, member.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        // "Which groups is this account in" (M7-3's visibility and M7-5's attention count).
        builder.HasIndex(member => member.AccountId);
    }
}

/// <summary>Table <c>CaseGroupMemberChanges</c>: append-only history of additions and removals. The
/// account ids have no foreign key, so the history outlives the accounts.</summary>
internal sealed class CaseGroupMemberChangeConfiguration : IEntityTypeConfiguration<CaseGroupMemberChange>
{
    public void Configure(EntityTypeBuilder<CaseGroupMemberChange> builder)
    {
        builder.ToTable("CaseGroupMemberChanges");
        builder.HasKey(change => change.Id);
        builder.Property(change => change.Id).ValueGeneratedNever();

        builder.HasOne<CaseGroup>()
            .WithMany()
            .HasForeignKey(change => new { change.GroupId, change.OrganizationId })
            .HasPrincipalKey(group => new { group.Id, group.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(change => new { change.GroupId, change.ChangedAt });
    }
}

/// <summary>Index names the case type endpoints recognize a violation of.</summary>
public static class CaseTypeIndexes
{
    /// <summary>The unique index on <c>(OrganizationId, Name)</c>, active or not.</summary>
    public const string Name = "IX_CaseTypes_OrganizationId_Name";
}

/// <summary>
/// Table <c>CaseTypes</c> (M7 plan §4; issue #247). The default group is a same-organization
/// composite foreign key with <c>Restrict</c>: a group is never deleted, and while a type points at
/// it the database refuses to. The handling time's range is also a check constraint.
/// </summary>
internal sealed class CaseTypeConfiguration : IEntityTypeConfiguration<CaseType>
{
    public void Configure(EntityTypeBuilder<CaseType> builder)
    {
        builder.ToTable("CaseTypes", table => table.HasCheckConstraint(
            "CK_CaseTypes_DefaultDueHours",
            $"\"DefaultDueHours\" BETWEEN {CaseType.MinDueHours} AND {CaseType.MaxDueHours}"));
        builder.HasKey(type => type.Id);
        builder.Property(type => type.Id).ValueGeneratedNever();
        builder.Property(type => type.Name).HasMaxLength(CaseType.NameMaxLength).IsRequired();
        builder.Property(type => type.Description).HasMaxLength(CaseType.DescriptionMaxLength).IsRequired();

        // Target of M7-3's composite foreign key from a case to its type.
        builder.HasAlternateKey(type => new { type.Id, type.OrganizationId });

        builder.HasOne<CaseGroup>()
            .WithMany()
            .HasForeignKey(type => new { type.DefaultGroupId, type.OrganizationId })
            .HasPrincipalKey(group => new { group.Id, group.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(type => new { type.OrganizationId, type.Name }).IsUnique().HasDatabaseName(CaseTypeIndexes.Name);
    }
}
