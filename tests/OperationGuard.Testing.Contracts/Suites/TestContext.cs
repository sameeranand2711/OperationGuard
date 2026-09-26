namespace OperationGuard.Testing.Contracts.Suites;

// xUnit v2 has no ambient per-test cancellation token. Keeping access centralized
// lets the suite adopt one without changing every provider contract signature.
internal sealed class TestContext
{
    public static TestContext Current { get; } = new();

    public CancellationToken CancellationToken => System.Threading.CancellationToken.None;
}
