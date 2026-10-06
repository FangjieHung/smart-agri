using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Secrets;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantLineChannels</c> (M5b plan §4): keyed by the assistant, at most one row each;
/// the composite foreign key keeps it in the assistant's organization and cascades.
/// <see cref="AssistantLineChannel.PublishedByAccountId"/> has no foreign key, as on
/// <c>AssistantWebsiteChannels</c>.
/// </summary>
/// <remarks>
/// <para>
/// The first table to store a <see cref="ProtectedSecret"/>: each one is an EF Core complex property
/// of three columns (<c>ChannelSecret_Ciphertext</c>, <c>ChannelSecret_LastFour</c>,
/// <c>ChannelSecret_SetAt</c>; the same for <c>AccessToken</c>). Both are required — a row exists only
/// after a first save, which needs both credentials.
/// </para>
/// <para>
/// <see cref="AssistantLineChannel.ConnectionChecks"/> is <c>jsonb</c> in the API's own shape
/// (camelCase, enum wire names: <c>[{"check":"access-token","state":"passed","message":…}]</c>),
/// always read and written whole, as <c>DatabaseFormVersionConfiguration.Fields</c>.
/// </para>
/// </remarks>
internal sealed class AssistantLineChannelConfiguration : IEntityTypeConfiguration<AssistantLineChannel>
{
    private static readonly JsonSerializerOptions ChecksJson = new(JsonSerializerDefaults.Web);

    private static readonly ValueComparer<IReadOnlyList<LineConnectionCheck>> ChecksComparer = new(
        (left, right) => Serialize(left) == Serialize(right),
        checks => Serialize(checks).GetHashCode(StringComparison.Ordinal),
        checks => Deserialize(Serialize(checks)));

    public void Configure(EntityTypeBuilder<AssistantLineChannel> builder)
    {
        builder.ToTable("AssistantLineChannels", table =>
        {
            table.HasCheckConstraint("CK_AssistantLineChannels_Revision", "\"Revision\" >= 1");
            // A draft has no publication; an enabled or paused channel always has one.
            table.HasCheckConstraint(
                "CK_AssistantLineChannels_Published",
                "(\"State\" = 'draft') = (\"PublishedAt\" IS NULL AND \"PublishedByAccountId\" IS NULL)");
            table.HasCheckConstraint("CK_AssistantLineChannels_ConnectionChecks", "jsonb_typeof(\"ConnectionChecks\") = 'array'");
            // Results and their time come and go together (a settings change clears both).
            table.HasCheckConstraint(
                "CK_AssistantLineChannels_ConnectionCheckedAt",
                "(\"ConnectionCheckedAt\" IS NULL) = (\"ConnectionChecks\" = '[]'::jsonb)");
            // Only a tested channel can have been enabled (the publishing gate needs every check passed).
            table.HasCheckConstraint(
                "CK_AssistantLineChannels_TestedBeforePublished",
                "\"State\" = 'draft' OR \"ConnectionCheckedAt\" IS NOT NULL");
            table.HasCheckConstraint(
                "CK_AssistantLineChannels_PushFallback",
                "\"PushFallbackCount\" >= 0 AND (\"PushFallbackMonth\" IS NULL) = (\"PushFallbackCount\" = 0)");
        });
        builder.HasKey(channel => channel.AssistantId);
        builder.Property(channel => channel.AssistantId).ValueGeneratedNever();
        builder.Property(channel => channel.OfficialAccountId)
            .HasMaxLength(AssistantLineChannel.OfficialAccountIdMaxLength)
            .IsRequired();
        builder.Property(channel => channel.ChannelId)
            .HasMaxLength(AssistantLineChannel.ChannelIdLength)
            .IsRequired();
        builder.ComplexProperty(channel => channel.ChannelSecret, ConfigureSecret);
        builder.ComplexProperty(channel => channel.AccessToken, ConfigureSecret);
        builder.Property(channel => channel.BotUserId).HasMaxLength(AssistantLineChannel.BotUserIdMaxLength);
        builder.Property(channel => channel.WelcomeMessage)
            .HasMaxLength(AssistantLineChannel.WelcomeMessageMaxLength)
            .IsRequired();
        builder.Property(channel => channel.State)
            .HasConversion<WireNameConverter<LineChannelState>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(channel => channel.ConnectionChecks)
            .HasConversion(
                new ValueConverter<IReadOnlyList<LineConnectionCheck>, string>(
                    checks => Serialize(checks),
                    json => Deserialize(json)),
                ChecksComparer)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(channel => channel.PushFallbackMonth).HasMaxLength(7);
        builder.Property(channel => channel.Revision).IsRequired();
        builder.Ignore(channel => channel.ConnectionChecksPassed);

        builder.HasOne<Assistant>()
            .WithOne()
            .HasForeignKey<AssistantLineChannel>(channel => new { channel.AssistantId, channel.OrganizationId })
            .HasPrincipalKey<Assistant>(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureSecret(ComplexPropertyBuilder<ProtectedSecret> secret)
    {
        secret.IsRequired();
        secret.Property(value => value.Ciphertext).IsRequired();
        secret.Property(value => value.LastFour).HasMaxLength(16);
        secret.Property(value => value.SetAt).IsRequired();
    }

    private static string Serialize(IReadOnlyList<LineConnectionCheck>? checks) =>
        JsonSerializer.Serialize(checks ?? [], ChecksJson);

    private static List<LineConnectionCheck> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<LineConnectionCheck>>(json, ChecksJson) ?? [];
}
