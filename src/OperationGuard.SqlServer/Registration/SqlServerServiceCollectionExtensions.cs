using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using OperationGuard.Core.Abstractions;
using OperationGuard.SqlServer.Stores;

namespace OperationGuard.SqlServer.Registration;

public static class SqlServerServiceCollectionExtensions
{
    public static IServiceCollection AddSqlServerOperationGuardStore(
        this IServiceCollection services,
        Func<DbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        services.AddSingleton(new SqlServerOperationStore(connectionFactory));
        services.AddSingleton<IOperationStore>(provider => provider.GetRequiredService<SqlServerOperationStore>());
        return services;
    }
}
