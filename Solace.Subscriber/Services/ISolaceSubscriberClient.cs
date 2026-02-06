using Solace.Shared;
using Solace.Shared.Messaging;

namespace Solace.Subscriber.Services;

public enum SubscriberReceiveMode
{
    DirectTopic,
    DurableQueue
}

public interface ISolaceSubscriberClient
{
    event Action? ConnectionChanged;

    SolaceOptions Options { get; }

    ConnectionSnapshot Connection { get; }

    string ClientName { get; }

    string SubscriptionTopic { get; }

    SubscriberReceiveMode ActiveReceiveMode { get; }

    string? ActiveQueueName { get; }

    int AckDelayMs { get; set; }

    Task<bool> ConnectAsync(SubscriberReceiveMode mode, string? queueName, CancellationToken cancellationToken = default);

    Task<bool> DisconnectAsync(CancellationToken cancellationToken = default);

    Task<bool> SimulateConnectionLossAsync(CancellationToken cancellationToken = default);
}
