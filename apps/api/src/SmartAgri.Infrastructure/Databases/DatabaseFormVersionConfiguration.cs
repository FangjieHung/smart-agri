using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Infrastructure.Databases;

/// <summary>
/// Table <c>DatabaseFormVersions</c>: one row per version of a database's form, never updated.
/// <see cref="DatabaseFormVersion.Fields"/> is <c>jsonb</c> in the API's own shape (camelCase,
/// enum wire names: <c>[{"id":"field-…","label":…,"type":"scale",…}]</c>) — always read and
/// written whole, never queried by content (mirrors <c>ChatMessageConfiguration.NextSteps</c>).
/// </summary>
internal sealed class DatabaseFormVersionConfiguration : IEntityTypeConfiguration<DatabaseFormVersion>
{
    private static readonly JsonSerializerOptions FieldsJson = new(JsonSerializerDefaults.Web);

    private static readonly ValueComparer<IReadOnlyList<DatabaseFormField>> FieldsComparer = new(
        (left, right) => Serialize(left) == Serialize(right),
        fields => Serialize(fields).GetHashCode(StringComparison.Ordinal),
        fields => Deserialize(Serialize(fields)));

    public void Configure(EntityTypeBuilder<DatabaseFormVersion> builder)
    {
        builder.ToTable("DatabaseFormVersions", table =>
        {
            table.HasCheckConstraint("CK_DatabaseFormVersions_VersionNumber", "\"VersionNumber\" >= 1");
            table.HasCheckConstraint("CK_DatabaseFormVersions_Fields", "jsonb_typeof(\"Fields\") = 'array'");
        });
        builder.HasKey(version => version.Id);
        builder.Property(version => version.Id).ValueGeneratedNever();
        builder.Property(version => version.Fields)
            .HasConversion(
                new ValueConverter<IReadOnlyList<DatabaseFormField>, string>(
                    fields => Serialize(fields),
                    json => Deserialize(json)),
                FieldsComparer)
            .HasColumnType("jsonb")
            .IsRequired();

        // Deleting a database deletes its form versions.
        builder.HasOne<Database>()
            .WithMany()
            .HasForeignKey(version => new { version.DatabaseId, version.OrganizationId })
            .HasPrincipalKey(database => new { database.Id, database.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // Version numbers are consecutive per database; two concurrent saves of the same next
        // number fail here instead of forking the form (#143). Also serves "current version".
        builder.HasIndex(version => new { version.DatabaseId, version.VersionNumber }).IsUnique();

        // Target for #145's submissions, which must point at a version of the same organization.
        builder.HasAlternateKey(version => new { version.Id, version.OrganizationId });
    }

    private static string Serialize(IReadOnlyList<DatabaseFormField>? fields) =>
        JsonSerializer.Serialize(fields ?? [], FieldsJson);

    private static List<DatabaseFormField> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<DatabaseFormField>>(json, FieldsJson) ?? [];
}
