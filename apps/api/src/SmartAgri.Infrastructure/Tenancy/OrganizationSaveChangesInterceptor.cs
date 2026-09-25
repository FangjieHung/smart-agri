using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Infrastructure.Tenancy;

/// <summary>
/// Write-side half of organization isolation (the read side is the <c>"Organization"</c>
/// query filter). Before <c>SaveChanges</c> sends anything to the database:
/// <list type="bullet">
/// <item>an added organization-scoped row with an empty <c>OrganizationId</c> gets the
/// current organization;</item>
/// <item>any added, modified or deleted organization-scoped row whose
/// <c>OrganizationId</c> (current or original) is not the current organization — or any
/// such row at all when there is no current organization — aborts the whole save with
/// <see cref="CrossOrganizationWriteException"/>, so nothing is written.</item>
/// </list>
/// Stateless: it reads the organization from the <see cref="AppDbContext"/> being saved,
/// so one shared instance serves every context.
/// </summary>
public sealed class OrganizationSaveChangesInterceptor : SaveChangesInterceptor
{
    private const string OrganizationIdProperty = nameof(IOrganizationScoped.OrganizationId);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Enforce(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Enforce(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Enforce(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        if (context is not AppDbContext appDbContext)
        {
            throw new InvalidOperationException(
                $"{nameof(OrganizationSaveChangesInterceptor)} only supports {nameof(AppDbContext)}.");
        }

        Enforce(context.ChangeTracker, appDbContext.OrganizationContext);
    }

    internal static void Enforce(ChangeTracker changeTracker, IOrganizationContext organizationContext)
    {
        // SaveChanges runs interceptors before its own DetectChanges; do it now so edits
        // made to tracked entities without notifying EF are checked too.
        changeTracker.DetectChanges();

        var currentOrganizationId = organizationContext.OrganizationId;

        foreach (var entry in changeTracker.Entries<IOrganizationScoped>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    var organizationId = entry.Property(OrganizationIdProperty);
                    if ((Guid)organizationId.CurrentValue! == Guid.Empty && currentOrganizationId is { } fill)
                    {
                        organizationId.CurrentValue = fill;
                    }

                    Require(entry, (Guid)organizationId.CurrentValue!, currentOrganizationId, "add");
                    break;

                case EntityState.Modified:
                    var modified = entry.Property(OrganizationIdProperty);
                    Require(entry, (Guid)modified.OriginalValue!, currentOrganizationId, "modify");
                    Require(entry, (Guid)modified.CurrentValue!, currentOrganizationId, "modify");
                    break;

                case EntityState.Deleted:
                    Require(entry, (Guid)entry.Property(OrganizationIdProperty).OriginalValue!, currentOrganizationId, "delete");
                    break;
            }
        }
    }

    private static void Require(
        EntityEntry entry,
        Guid rowOrganizationId,
        Guid? currentOrganizationId,
        string operation)
    {
        if (currentOrganizationId is null)
        {
            throw new CrossOrganizationWriteException(
                $"Cannot {operation} a {entry.Metadata.DisplayName()} row without a current organization.");
        }

        if (rowOrganizationId != currentOrganizationId.Value)
        {
            throw new CrossOrganizationWriteException(
                $"Cannot {operation} a {entry.Metadata.DisplayName()} row that belongs to another organization.");
        }
    }
}
