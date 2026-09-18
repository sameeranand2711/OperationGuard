using System.Security.Cryptography;
using System.Text;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.Testing.Contracts.Suites;

public abstract class FingerprintingContract<TFactory>
    where TFactory : ICoreContractDriverFactory, new()
{
    private readonly ICoreContractDriver _driver = new TFactory().CreateDriver();

    [Fact]
    public void Default_digest_is_sha256_and_versioned()
    {
        var fingerprint = Create(body: "{}");

        Assert.Equal("SHA-256", fingerprint.Algorithm);
        Assert.True(fingerprint.Version > 0);
        Assert.Equal(64, fingerprint.Digest.Length);
        Assert.Equal(32, Convert.FromHexString(fingerprint.Digest).Length);
    }

    [Fact]
    public void Operation_method_content_type_and_selected_headers_are_semantic_inputs()
    {
        var baseline = Create(body: "{}");

        Assert.NotEqual(baseline, Create(body: "{}", operationName: "Payments.Update"));
        Assert.NotEqual(baseline, Create(body: "{}", method: "PUT"));
        Assert.NotEqual(baseline, Create(body: "{}", contentType: "application/vnd.test+json"));
        Assert.NotEqual(
            baseline,
            Create(body: "{}", headers: new Dictionary<string, string[]> { ["X-Business-Version"] = ["2"] }));
    }

    [Fact]
    public void Query_parameter_order_is_canonicalized()
    {
        var first = Create(body: "{}", query: "?b=2&a=1");
        var reordered = Create(body: "{}", query: "?a=1&b=2");

        Assert.Equal(first, reordered);
    }

    [Fact]
    public void Repeated_query_value_order_is_preserved()
    {
        var first = Create(body: "{}", query: "?item=a&item=b");
        var reversed = Create(body: "{}", query: "?item=b&item=a");

        Assert.NotEqual(first, reversed);
    }

    [Fact]
    public void Raw_json_property_order_is_not_silently_canonicalized()
    {
        var amountFirst = Create(body: "{\"amount\":100,\"currency\":\"USD\"}");
        var currencyFirst = Create(body: "{\"currency\":\"USD\",\"amount\":100}");

        Assert.NotEqual(amountFirst, currencyFirst);
    }

    [Fact]
    public void Default_fingerprint_rejects_body_above_one_mebibyte()
    {
        Assert.ThrowsAny<ArgumentException>(() => Create(new string('x', 1024 * 1024 + 1)));
    }

    [Fact]
    public async Task Custom_provider_is_used_and_observes_cancellation()
    {
        var expected = new ContractFingerprint("business-fields-v1", 1, "stable-business-id");
        var provider = new RecordingProvider(expected);
        using var source = new CancellationTokenSource();
        var request = Request("{}");

        var actual = await _driver.FingerprintWithCustomProviderAsync(request, provider, source.Token);

        Assert.Equal(expected, actual);
        Assert.Same(request, provider.Request);
        Assert.Equal(source.Token, provider.CancellationToken);
    }

    private ContractFingerprint Create(
        string body,
        string operationName = "Payments.Create",
        string method = "POST",
        string query = "?a=1&b=2",
        string contentType = "application/json",
        IReadOnlyDictionary<string, string[]>? headers = null) =>
        _driver.Fingerprint(operationName, method, query, contentType, Encoding.UTF8.GetBytes(body), headers);

    private static ContractHttpRequest Request(string body) => new(
        "tenant", "Payments.Create", "POST", "", "key", "application/json", Encoding.UTF8.GetBytes(body));

    private sealed class RecordingProvider(ContractFingerprint result) : IContractFingerprintProvider
    {
        public ContractHttpRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public ValueTask<ContractFingerprint> CreateAsync(
            ContractHttpRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            CancellationToken = cancellationToken;
            return ValueTask.FromResult(result);
        }
    }
}
