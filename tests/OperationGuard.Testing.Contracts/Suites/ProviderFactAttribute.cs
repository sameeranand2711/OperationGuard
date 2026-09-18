using Xunit;

namespace OperationGuard.Testing.Contracts.Suites;

public sealed class ProviderFactAttribute : FactAttribute
{
    public ProviderFactAttribute()
    {
        var sqlServer = Environment.GetEnvironmentVariable("OPERATIONGUARD_SQLSERVER_CONNECTION_STRING");
        var postgreSql = Environment.GetEnvironmentVariable("OPERATIONGUARD_POSTGRESQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(sqlServer) && string.IsNullOrWhiteSpace(postgreSql))
        {
            Skip = "Real provider contracts require SQL Server and PostgreSQL connection strings.";
        }
    }
}
