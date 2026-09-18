using Npgsql;

namespace PaymentApi;

internal static class PaymentSchema
{
    internal static async Task EnsureCreatedAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sample_payments
            (
                payment_id uuid PRIMARY KEY,
                tenant_scope text NOT NULL,
                account_id text NOT NULL,
                amount numeric(20, 4) NOT NULL,
                currency varchar(3) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
