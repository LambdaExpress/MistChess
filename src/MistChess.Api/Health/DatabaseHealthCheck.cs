using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MistChess.Infrastructure.Persistence;

namespace MistChess.Api.Health;

public sealed class DatabaseHealthCheck(IDbContextFactory<MistChessDbContext> contextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
            }

            var pendingMigrations = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            return pendingMigrations.Any()
                ? HealthCheckResult.Unhealthy("PostgreSQL has pending MistChess migrations.")
                : HealthCheckResult.Healthy("PostgreSQL is reachable and migrated.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL readiness check failed.", exception);
        }
    }
}
