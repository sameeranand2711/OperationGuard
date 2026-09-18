using Npgsql;
using OperationGuard.AspNetCore.Registration;
using OperationGuard.PostgreSql.Registration;
using OperationGuard.PostgreSql.Stores;
using PaymentApi;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("OperationGuard");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Configure ConnectionStrings:OperationGuard (or ConnectionStrings__OperationGuard) with a PostgreSQL connection string.");
}

builder.Services.AddOperationGuard(options =>
{
    options.ScopeResolver = PaymentEndpoint.ResolveTenantScopeAsync;
    options.ReplayHeaders = ["Content-Type", "Location"];
});
builder.Services.AddPostgreSqlOperationGuardStore(() => new NpgsqlConnection(connectionString));

var app = builder.Build();

var store = app.Services.GetRequiredService<PostgreSqlOperationStore>();
await store.EnsureCreatedAsync();
await PaymentSchema.EnsureCreatedAsync(connectionString);

// This endpoint deliberately uses the provider's transaction-session API directly. Do not also
// apply RequireOperationGuard here: the V1 HTTP middleware has a separate transaction boundary.
// The sample performs only local PostgreSQL writes; adding a remote charge would create an
// external-side-effect topology that OperationGuard cannot make exactly once.
app.MapPost("/payments", PaymentEndpoint.HandleAsync);

app.Run();
