using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>Stores <see cref="ModelInvocation"/> rows (an interface so the middleware's rules are
/// unit tested without a database).</summary>
public interface IModelInvocationRecorder
{
    /// <summary>Saves <paramref name="invocation"/> now, whatever the caller does afterwards.</summary>
    Task RecordAsync(ModelInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// Saves each row through a context of its own, for the given organization (so the write guard
/// checks it like any other row), committed at once: an audit row must not depend on the
/// caller's transaction — a processing run whose result is rolled back still made its calls.
/// </summary>
public sealed class EfModelInvocationRecorder : IModelInvocationRecorder
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IOrganizationContext _organization;

    public EfModelInvocationRecorder(DbContextOptions<AppDbContext> options, IOrganizationContext organization)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(organization);
        _options = options;
        _organization = organization;
    }

    public async Task RecordAsync(ModelInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        await using var dbContext = new AppDbContext(_options, _organization);
        dbContext.ModelInvocations.Add(invocation);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
