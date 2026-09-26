using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OperationGuard.AspNetCore.Metadata;
using OperationGuard.Core;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Fingerprinting;
using OperationGuard.Core.Models;
using OperationGuard.Core.Testing;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.AspNetCore.Tests;

public sealed class HttpContractDriverFactory : IHttpContractDriverFactory
{
    public ValueTask<IHttpContractDriver> CreateDriverAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IHttpContractDriver>(new Driver());

    private sealed class Driver : IHttpContractDriver
    {
        private readonly InMemoryOperationStore _store = new();
        private readonly OperationGuardAspNetCoreOptions _options = new()
        {
            ScopeResolver = static (context, _) => ValueTask.FromResult((string)context.Items["scope"]!),
        };
        private readonly OperationGuardOptions _coreOptions = new();

        public int HandlerInvocationCount { get; private set; }

        public ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _store.Reset();
            HandlerInvocationCount = 0;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<ContractHttpResponse> SendAsync(
            ContractHttpRequest request,
            ContractHandlerResponse handlerResponse,
            CancellationToken cancellationToken)
        {
            var context = CreateContext(request, cancellationToken);
            RequestDelegate handler = async httpContext =>
            {
                HandlerInvocationCount++;
                httpContext.Response.StatusCode = handlerResponse.StatusCode;
                foreach (var header in handlerResponse.Headers)
                {
                    httpContext.Response.Headers[header.Key] = header.Value;
                }

                await httpContext.Response.Body.WriteAsync(handlerResponse.Body, cancellationToken);
                if (handlerResponse.ThrowAfterPossibleSideEffect)
                {
                    throw new InvalidOperationException("Injected ambiguous failure.");
                }
            };
            var middleware = new OperationGuardMiddleware(
                handler,
                _options,
                _coreOptions,
                TimeProvider.System,
                NullLogger<OperationGuardMiddleware>.Instance);
            IRequestFingerprintProvider fingerprintProvider = request.UseCustomFingerprint
                ? new SemanticJsonFingerprintProvider()
                : new Sha256RequestFingerprintProvider();
            await middleware.InvokeAsync(context, _store, fingerprintProvider);
            context.Response.Body.Position = 0;
            using var copy = new MemoryStream();
            await context.Response.Body.CopyToAsync(copy, cancellationToken);
            var problemType = TryReadProblemType(copy.ToArray());
            return new ContractHttpResponse(
                context.Response.StatusCode,
                context.Response.Headers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(value => value!).ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                copy.ToArray(),
                problemType);
        }

        public async ValueTask SeedInProgressAsync(ContractHttpRequest request, CancellationToken cancellationToken)
        {
            var fingerprint = await CreateFingerprintAsync(request, cancellationToken);
            await _store.TryBeginAsync(
                Identity(request),
                fingerprint,
                TimeProvider.System.GetUtcNow(),
                TimeSpan.FromMinutes(15),
                cancellationToken);
        }

        public async ValueTask<ContractStoredOperation?> ReadStoredOperationAsync(
            ContractIdentity identity,
            CancellationToken cancellationToken)
        {
            var operation = await _store.ReadOutcomeAsync(
                new OperationIdentity(identity.Scope, identity.OperationName, identity.IdempotencyKey),
                cancellationToken);
            return ToContract(operation);
        }

        public ValueTask SetStoreAvailabilityAsync(bool available, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _store.Available = available;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private DefaultHttpContext CreateContext(ContractHttpRequest request, CancellationToken cancellationToken)
        {
            var context = new DefaultHttpContext();
            context.RequestAborted = cancellationToken;
            context.Items["scope"] = request.Scope;
            context.Request.Method = request.Method;
            context.Request.QueryString = new QueryString(request.Query);
            context.Request.ContentType = request.ContentType;
            context.Request.ContentLength = request.Body.Length;
            context.Request.Body = new MemoryStream(request.Body);
            if (request.IdempotencyKey is not null)
            {
                context.Request.Headers["Idempotency-Key"] = request.IdempotencyKey;
            }

            if (request.Headers is not null)
            {
                foreach (var header in request.Headers)
                {
                    context.Request.Headers[header.Key] = header.Value;
                }
            }

            context.Response.Body = new MemoryStream();
            context.SetEndpoint(new Endpoint(
                null,
                new EndpointMetadataCollection(new OperationGuardEndpointMetadata(request.OperationName)),
                request.OperationName));
            return context;
        }

        private async ValueTask<OperationFingerprint> CreateFingerprintAsync(
            ContractHttpRequest request,
            CancellationToken cancellationToken)
        {
            IRequestFingerprintProvider provider = request.UseCustomFingerprint
                ? new SemanticJsonFingerprintProvider()
                : new Sha256RequestFingerprintProvider();
            return await provider.CreateAsync(
                new FingerprintInput(
                    request.OperationName,
                    request.Method,
                    request.Query,
                    request.ContentType,
                    request.Body,
                    request.Headers),
                cancellationToken);
        }

        private static OperationIdentity Identity(ContractHttpRequest request) =>
            new(request.Scope, request.OperationName, request.IdempotencyKey!);

        private static string? TryReadProblemType(byte[] body)
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                return json.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static ContractStoredOperation? ToContract(StoredOperation? operation) => operation is null
            ? null
            : new ContractStoredOperation(
                new ContractIdentity(operation.Identity.Scope, operation.Identity.OperationName, operation.Identity.IdempotencyKey),
                new ContractFingerprint(operation.Fingerprint.Algorithm, operation.Fingerprint.Version, operation.Fingerprint.Digest),
                (ContractOperationState)operation.State,
                operation.OwnerToken,
                operation.CreatedAt,
                operation.LeaseExpiresAt,
                operation.RetainUntil,
                operation.Response is null
                    ? null
                    : new ContractReplayResponse(operation.Response.StatusCode, operation.Response.Headers, operation.Response.Body),
                operation.ReplayBodyAvailable,
                operation.ResponseDigest,
                operation.RecoveryVersion);
    }

    private sealed class SemanticJsonFingerprintProvider : IRequestFingerprintProvider
    {
        public ValueTask<OperationFingerprint> CreateAsync(FingerprintInput input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(input.Body);
            var semantic = string.Join('|', document.RootElement.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{property.Name}:{property.Value.GetRawText()}"));
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semantic))).ToLowerInvariant();
            return ValueTask.FromResult(OperationFingerprint.Sha256(digest));
        }
    }
}
