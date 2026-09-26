using MessageConsumer;
using Npgsql;
using OperationGuard.PostgreSql.Stores;

var connectionString = Environment.GetEnvironmentVariable("OPERATIONGUARD_POSTGRESQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Set OPERATIONGUARD_POSTGRESQL_CONNECTION_STRING to the PostgreSQL database shared by the guard and business tables.");
}

var store = new PostgreSqlOperationStore(() => new NpgsqlConnection(connectionString));
await store.EnsureCreatedAsync();
await MessageSchema.EnsureCreatedAsync(connectionString, CancellationToken.None);

var message = new IncomingMessage(
    MessageId: args.ElementAtOrDefault(0) ?? "sample-message-001",
    TenantScope: args.ElementAtOrDefault(1) ?? "sample-tenant",
    AccountId: args.ElementAtOrDefault(2) ?? "account-42",
    Amount: decimal.TryParse(args.ElementAtOrDefault(3), out var amount) ? amount : 10m);

var processor = new TransactionalMessageProcessor(store, connectionString, TimeProvider.System);
var outcome = await processor.HandleAsync(message, CancellationToken.None);
Console.WriteLine($"{message.MessageId}: {outcome}");

// A broker adapter should acknowledge Processed or Duplicate only after HandleAsync returns.
// It should defer/dead-letter conflicts and indeterminate outcomes according to application policy;
// OperationGuard does not automatically retry an ambiguous operation.
