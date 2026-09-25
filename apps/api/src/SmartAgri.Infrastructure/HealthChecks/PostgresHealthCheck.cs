using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartAgri.Infrastructure.HealthChecks;

/// <summary>
/// Backs <c>GET /health/ready</c>: reports healthy only when the database is reachable.
/// Deliberately does not touch <c>/health/live</c>, which must stay up regardless of the
/// database (see M1 skeleton plan, Slice 2).
/// </summary>
public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly AppDbContext _dbContext;

    public PostgresHealthCheck(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);

            return canConnect
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Could not connect to the database.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Could not connect to the database.", ex);
        }
    }
}
