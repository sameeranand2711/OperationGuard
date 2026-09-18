using System.Diagnostics;
using OperationGuard.Core.Models;
using OperationGuard.Core.Testing;

var smoke = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
var suite = new OperationGuardBenchmarkSuite(smoke);
await suite.RunAsync();

internal sealed class OperationGuardBenchmarkSuite(bool smoke)
{
    private static readonly OperationFingerprint Fingerprint = OperationFingerprint.Sha256(new string('a', 64));
    private readonly int _serialIterations = smoke ? 100 : 20_000;
    private readonly int _contentionRounds = smoke ? 2 : 25;
    private long _keySequence;

    internal async Task RunAsync()
    {
        Console.WriteLine("OperationGuard in-memory benchmark harness");
        Console.WriteLine("These measurements exercise core paths and allocations; they are not production database throughput claims.");
        Console.WriteLine($"Mode: {(smoke ? "smoke" : "full")}");

        await MeasureAsync("first reservation", _serialIterations, FirstReservationAsync);
        await MeasureAsync("completed duplicate replay (1 KiB)", _serialIterations, iteration => CompletedDuplicateReplayAsync(iteration, 1024));
        await MeasureAsync("completed duplicate replay (64 KiB)", _serialIterations, iteration => CompletedDuplicateReplayAsync(iteration, 64 * 1024));
        await MeasureAsync("hot-key contention (100 callers)", _contentionRounds, iteration => HotKeyContentionAsync(iteration, 100));
        await MeasureAsync("independent keys (100 callers)", smoke ? 2 : 50, iteration => IndependentKeysAsync(iteration, 100));
        await MeasureAsync("independent keys (1000 callers)", smoke ? 1 : 10, iteration => IndependentKeysAsync(iteration, 1000));
    }

    private async Task FirstReservationAsync(int iteration)
    {
        var store = new InMemoryOperationStore();
        var result = await store.TryBeginAsync(
            Identity("first", iteration),
            Fingerprint,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(15));
        Require(result.Kind == OperationBeginKind.Acquired, "The first reservation was not acquired.");
    }

    private static async Task CompletedDuplicateReplayAsync(int iteration, int bodySize)
    {
        var store = ReplayFixture.ForBodySize(bodySize);
        var result = await store.Store.TryBeginAsync(
            store.Identity,
            Fingerprint,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(15));
        Require(result.Kind == OperationBeginKind.Completed, "The duplicate did not return Completed.");
        var body = result.Operation?.Response?.Body;
        Require(body?.Length == bodySize, "The replay body was unavailable or truncated.");
        await Stream.Null.WriteAsync(body);
    }

    private async Task HotKeyContentionAsync(int iteration, int callers)
    {
        var store = new InMemoryOperationStore();
        var identity = Identity("hot", iteration);
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await store.TryBeginAsync(
                identity,
                Fingerprint,
                DateTimeOffset.UnixEpoch,
                TimeSpan.FromMinutes(15));
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        Require(results.Count(result => result.Kind == OperationBeginKind.Acquired) == 1, "Hot-key contention produced more than one owner.");
        Require(results.All(result => result.Kind is OperationBeginKind.Acquired or OperationBeginKind.AlreadyInProgress), "Unexpected contention result.");
    }

    private async Task IndependentKeysAsync(int iteration, int callers)
    {
        var store = new InMemoryOperationStore();
        var tasks = Enumerable.Range(0, callers).Select(index => store.TryBeginAsync(
            Identity($"independent-{iteration}", index),
            Fingerprint,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(15)).AsTask());
        var results = await Task.WhenAll(tasks);
        Require(results.All(result => result.Kind == OperationBeginKind.Acquired), "An independent key was not acquired.");
    }

    private async Task MeasureAsync(string name, int iterations, Func<int, Task> operation)
    {
        await operation(-1);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            await operation(iteration);
        }

        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Console.WriteLine(
            $"{name,-43} {stopwatch.Elapsed.TotalMilliseconds / iterations,10:F3} ms/iteration  {allocated / (double)iterations,12:F0} B/iteration");
    }

    private OperationIdentity Identity(string scenario, int iteration) =>
        new("benchmark", scenario, $"{Interlocked.Increment(ref _keySequence)}-{iteration}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ReplayFixture(InMemoryOperationStore Store, OperationIdentity Identity)
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<int, ReplayFixture> Fixtures = [];

        internal static ReplayFixture ForBodySize(int bodySize)
        {
            lock (Sync)
            {
                if (Fixtures.TryGetValue(bodySize, out var existing))
                {
                    return existing;
                }

                var store = new InMemoryOperationStore();
                var identity = new OperationIdentity("benchmark", "replay", $"body-{bodySize}");
                var begin = store.TryBeginAsync(
                    identity,
                    Fingerprint,
                    DateTimeOffset.UnixEpoch,
                    TimeSpan.FromMinutes(15)).GetAwaiter().GetResult();
                var body = Enumerable.Repeat((byte)'x', bodySize).ToArray();
                var response = new ReplayResponse(
                    200,
                    new Dictionary<string, string[]> { ["Content-Type"] = ["application/octet-stream"] },
                    body);
                var completed = store.CompleteAsync(
                    identity,
                    begin.OwnerToken!,
                    response,
                    replayBodyAvailable: true,
                    responseDigest: null,
                    DateTimeOffset.UnixEpoch.AddHours(24)).GetAwaiter().GetResult();
                Require(completed == ConditionalWriteKind.Applied, "Replay fixture could not be completed.");
                var fixture = new ReplayFixture(store, identity);
                Fixtures.Add(bodySize, fixture);
                return fixture;
            }
        }
    }
}
