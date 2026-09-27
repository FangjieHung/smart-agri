using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SmartAgri.Infrastructure.Persistence;

/// <summary>Recognizes PostgreSQL errors callers handle, without them depending on Npgsql.</summary>
public static class DatabaseErrors
{
    /// <summary>Whether a save failed because it would have broken a unique index or
    /// constraint (e.g. two concurrent requests passed the same application check).</summary>
    public static bool IsUniqueViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    /// <summary>Whether a save failed because a row it refers to is gone (e.g. a document
    /// deleted by a concurrent request while a new version of it was being saved).</summary>
    public static bool IsForeignKeyViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };
    }
}
