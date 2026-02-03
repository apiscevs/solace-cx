namespace Solace.Shared.Messaging;

public sealed record MessageRecord(
    DateTimeOffset TimestampUtc,
    MessageDirection Direction,
    string Topic,
    string Payload,
    bool Success,
    string Details);
