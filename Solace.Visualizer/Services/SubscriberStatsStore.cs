using System.Collections.Concurrent;

namespace Solace.Visualizer.Services;

public sealed class SubscriberStatsStore
{
    private readonly ConcurrentDictionary<string, SubscriberStat> _stats = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public IReadOnlyDictionary<string, SubscriberStat> Snapshot =>
        _stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

    public void Update(SubscriberStat stat)
    {
        _stats[stat.ClientName] = stat;
        Changed?.Invoke();
    }
}
