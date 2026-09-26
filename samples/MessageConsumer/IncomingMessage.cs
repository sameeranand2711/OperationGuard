namespace MessageConsumer;

internal sealed record IncomingMessage(
    string MessageId,
    string TenantScope,
    string AccountId,
    decimal Amount);
