using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Ai;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>
/// Table <c>ModelInvocations</c>: the model-call audit log. No column holds content (checked by
/// a model test). The account, when there is one, is an account of the same organization; there
/// is no assistant table yet (M3), so <see cref="ModelInvocation.AssistantId"/> has no key.
/// </summary>
internal sealed class ModelInvocationConfiguration : IEntityTypeConfiguration<ModelInvocation>
{
    public void Configure(EntityTypeBuilder<ModelInvocation> builder)
    {
        builder.ToTable("ModelInvocations");
        builder.HasKey(invocation => invocation.Id);
        builder.Property(invocation => invocation.Id).ValueGeneratedNever();
        builder.Property(invocation => invocation.Purpose)
            .HasConversion<WireNameConverter<ModelInvocationPurpose>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(invocation => invocation.Provider).HasMaxLength(ModelInvocation.ProviderMaxLength).IsRequired();
        builder.Property(invocation => invocation.Model).HasMaxLength(ModelInvocation.ModelMaxLength).IsRequired();

        // Restrict, like a version's uploader: accounts are not deleted in M2, and an audit row
        // must not disappear with its account.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(invocation => new { invocation.AccountId, invocation.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        // An organization's calls over time (usage reports, audits).
        builder.HasIndex(invocation => new { invocation.OrganizationId, invocation.At });
    }
}
