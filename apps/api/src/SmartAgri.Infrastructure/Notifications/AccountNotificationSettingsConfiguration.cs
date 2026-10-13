using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Notifications;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Infrastructure.Notifications;

/// <summary>
/// Table <c>AccountNotificationSettings</c> (#355): at most one row per account, keyed by it, with a
/// same-organization composite foreign key to <c>Accounts</c>. A separate table rather than columns on
/// <c>Accounts</c>, so the sign-in model stays as it is.
/// </summary>
internal sealed class AccountNotificationSettingsConfiguration : IEntityTypeConfiguration<AccountNotificationSettings>
{
    public void Configure(EntityTypeBuilder<AccountNotificationSettings> builder)
    {
        builder.ToTable("AccountNotificationSettings");
        builder.HasKey(settings => settings.AccountId);
        builder.Property(settings => settings.AccountId).ValueGeneratedNever();
        builder.Property(settings => settings.Email).HasMaxLength(AccountNotificationSettings.EmailMaxLength);
        builder.Ignore(settings => settings.HandoffEmailAddress);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(settings => new { settings.AccountId, settings.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
