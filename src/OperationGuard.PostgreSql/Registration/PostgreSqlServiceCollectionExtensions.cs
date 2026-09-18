using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using OperationGuard.Core.Abstractions;
using OperationGuard.PostgreSql.Stores;

namespace OperationGuard.PostgreSql.Registration;

public static class PostgreSqlServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlOperationGuardStore(
        this IServiceCollection services,
        Func<DbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        services.AddSingleton(new PostgreSqlOperationStore(connectionFactory));
        services.AddSingleton<IOperationStore>(provider => provider.GetRequiredService<PostgreSqlOperationStore>());
        return services;
    }
}
