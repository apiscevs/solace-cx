namespace Solace.Shared.Messaging;

public sealed record ConnectionSnapshot(bool IsConnected, string Status, string Details);
