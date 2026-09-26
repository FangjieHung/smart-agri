using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartAgri.Infrastructure;

/// <summary>
/// PostgreSQL stores timestamps to the microsecond, .NET to the 100-nanosecond tick. Before
/// <c>SaveChanges</c> sends anything, every added or modified <see cref="DateTimeOffset"/> /
/// <see cref="DateTime"/> value is truncated to the microsecond, so the tracked entity holds
/// exactly what the database will return. Without this, a response built from the entity
/// just saved (for example <c>updatedAt</c> after a create) differs from the same field read
/// back later — only on platforms whose clock has sub-microsecond resolution (Linux, not
/// macOS), which is how it first showed up: in CI, not locally.
/// </summary>
public sealed class TimestampPrecisionInterceptor : SaveChangesInterceptor
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMicrosecond;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Truncate(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Truncate(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Truncate(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // SaveChanges runs interceptors before its own DetectChanges.
        context.ChangeTracker.DetectChanges();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            foreach (var property in entry.Properties)
            {
                switch (property.CurrentValue)
                {
                    case DateTimeOffset value when value.Ticks % TicksPerMicrosecond != 0:
                        property.CurrentValue = value.AddTicks(-(value.Ticks % TicksPerMicrosecond));
                        break;
                    case DateTime value when value.Ticks % TicksPerMicrosecond != 0:
                        property.CurrentValue = value.AddTicks(-(value.Ticks % TicksPerMicrosecond));
                        break;
                }
            }
        }
    }
}
