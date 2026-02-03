using Solace.Shared;
using Solace.Shared.Messaging;

namespace Solace.Publisher.Services;

public interface ISolacePublisherClient
{
    event Action? ConnectionChanged;

    SolaceOptions Options { get; }

    ConnectionSnapshot Connection { get; }

    Task<bool> PublishAsync(string topic, string payload, CancellationToken cancellationToken = default);
}
