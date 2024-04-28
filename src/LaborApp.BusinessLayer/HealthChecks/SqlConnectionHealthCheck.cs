using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace LaborApp.BusinessLayer.HealthChecks;

public class SqlConnectionHealthCheck : IHealthCheck
{
    private readonly IConfiguration configuration;
    private readonly ILogger<SqlConnectionHealthCheck> logger;

    public SqlConnectionHealthCheck(IConfiguration configuration, ILogger<SqlConnectionHealthCheck> logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = configuration.GetConnectionString("SqlConnection");
            var connection = new SqlConnection(connectionString);

            await connection.OpenAsync(cancellationToken);
            await connection.CloseAsync();
            await connection.DisposeAsync();

            return HealthCheckResult.Healthy();
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Error occurred while connecting to database");
            return HealthCheckResult.Unhealthy(ex.Message, ex);
        }
    }
}