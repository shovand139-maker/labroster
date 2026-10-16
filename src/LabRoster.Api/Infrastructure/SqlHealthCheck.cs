using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LabRoster.Api.Infrastructure;

/// <summary>Readiness probe: the app login can reach the database.</summary>
public sealed class SqlHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(configuration.GetConnectionString("LabRoster"));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1;", connection) { CommandTimeout = 5 };
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("The database is not reachable.", ex);
        }
    }
}
