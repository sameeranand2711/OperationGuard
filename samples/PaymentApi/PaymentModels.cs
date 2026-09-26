using System.Text.Json.Serialization;

namespace PaymentApi;

internal sealed record PaymentRequest(string AccountId, decimal Amount, string Currency);

internal sealed record PaymentResponse(Guid PaymentId, string Status);

internal sealed record NormalizedPayment(string AccountId, decimal Amount, string Currency);

[JsonSerializable(typeof(PaymentResponse))]
internal sealed partial class PaymentJsonContext : JsonSerializerContext;
