using Npgsql;

namespace MessageConsumer;

internal static class MessageSchema
{
    internal static async Task EnsureCreatedAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sample_message_credits
            (
                message_id text NOT NULL,
                tenant_scope text NOT NULL,
                account_id text NOT NULL,
                amount numeric(20, 4) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (tenant_scope, message_id)
            )
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
