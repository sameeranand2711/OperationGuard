using Microsoft.AspNetCore.Authentication;
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

// Demo/local authentication only: the opaque key is supplied through configuration and maps to
// one server-configured tenant. No credential is committed, and callers cannot select tenant_id.
var sampleAuthentication = SampleAuthenticationSettings.FromConfiguration(builder.Configuration);

builder.Services.AddOperationGuard(options =>
{
    options.ScopeResolver = PaymentEndpoint.ResolveTenantScopeAsync;
    options.ReplayHeaders = ["Content-Type", "Location"];
});
builder.Services.AddPostgreSqlOperationGuardStore(() => new NpgsqlConnection(connectionString));
builder.Services.AddSingleton(sampleAuthentication);
builder.Services
    .AddAuthentication(SampleApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SampleApiKeyAuthenticationHandler>(
        SampleApiKeyAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

var store = app.Services.GetRequiredService<PostgreSqlOperationStore>();
await store.EnsureCreatedAsync();
await PaymentSchema.EnsureCreatedAsync(connectionString);

// This endpoint deliberately uses the provider's transaction-session API directly. Do not also
// apply RequireOperationGuard here: the V1 HTTP middleware has a separate transaction boundary.
// The sample performs only local PostgreSQL writes; adding a remote charge would create an
// external-side-effect topology that OperationGuard cannot make exactly once.
app.MapPost("/payments", PaymentEndpoint.HandleAsync)
    .RequireAuthorization();

app.Run();
