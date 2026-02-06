using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Solace.Shared;
using SolaceSystems.Solclient.Messaging;
using SolaceSession = SolaceSystems.Solclient.Messaging.ISession;

namespace Solace.Visualizer.Services;

public sealed class SolaceStatsListener(
    IOptions<SolaceOptions> options,
    SubscriberStatsStore store,
    ILogger<SolaceStatsListener> logger) : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object FactorySync = new();
    private static bool _factoryInitialized;

    private readonly SolaceOptions _options = options.Value;
    private readonly SubscriberStatsStore _store = store;
    private readonly string _clientName = $"solace-visualizer-{Environment.ProcessId}";

    private IContext? _context;
    private SolaceSession? _session;
    private bool _isSubscribed;
    private bool _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureFactoryInitialized();

        try
        {
            _context = ContextFactory.Instance.CreateContext(new ContextProperties(), null);
            _session = _context.CreateSession(BuildSessionProperties(), OnStatsMessageReceived, OnSessionEvent);
            _session.Connect();
            SubscribeToStatsTopic();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Visualizer failed to connect to Solace for stats.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeSession();
        return Task.CompletedTask;
    }

    private SessionProperties BuildSessionProperties()
    {
        return new SessionProperties
        {
            Host = _options.Host,
            VPNName = _options.VpnName,
            UserName = _options.Username,
            Password = _options.Password,
            ClientName = _clientName,
            ConnectBlocking = true,
            ConnectRetries = 3,
            ConnectRetriesPerHost = 3,
            ReconnectRetries = 20,
            ReconnectRetriesWaitInMsecs = 3_000,
            ReapplySubscriptions = true,
            IgnoreDuplicateSubscriptionError = true,
            SSLValidateCertificate = false
        };
    }

    private void SubscribeToStatsTopic()
    {
        if (_session is null || _isSubscribed)
        {
            return;
        }

        var topic = $"{_options.StatsTopicPrefix}/>";
        _session.Subscribe(ContextFactory.Instance.CreateTopic(topic), false);
        _isSubscribed = true;
    }

    private void OnStatsMessageReceived(object? sender, MessageEventArgs args)
    {
        try
        {
            var bytes = args.Message.BinaryAttachment;
            if (bytes is null || bytes.Length == 0)
            {
                return;
            }

            var json = Encoding.UTF8.GetString(bytes);
            var stat = JsonSerializer.Deserialize<SubscriberStat>(json, JsonOptions);
            if (stat is not null)
            {
                _store.Update(stat);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse subscriber stats message.");
        }
        finally
        {
            args.Message.Dispose();
        }
    }

    private void OnSessionEvent(object? sender, SessionEventArgs args)
    {
        if (_session is null)
        {
            return;
        }

        switch (args.Event)
        {
            case SessionEvent.UpNotice:
            case SessionEvent.Reconnected:
                _isSubscribed = false;
                SubscribeToStatsTopic();
                break;
            case SessionEvent.Reconnecting:
            case SessionEvent.ConnectFailedError:
            case SessionEvent.DownError:
                _isSubscribed = false;
                break;
        }
    }

    private void DisposeSession()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _session?.Dispose();
        }
        catch
        {
            // ignore dispose errors
        }

        try
        {
            _context?.Dispose();
        }
        catch
        {
            // ignore dispose errors
        }

        _session = null;
        _context = null;

        CleanupFactory();
    }

    public void Dispose()
    {
        DisposeSession();
        GC.SuppressFinalize(this);
    }

    private static void EnsureFactoryInitialized()
    {
        if (_factoryInitialized)
        {
            return;
        }

        lock (FactorySync)
        {
            if (_factoryInitialized)
            {
                return;
            }

            ContextFactory.Instance.Init(new ContextFactoryProperties { SolClientLogLevel = SolLogLevel.Warning });
            _factoryInitialized = true;
        }
    }

    private static void CleanupFactory()
    {
        lock (FactorySync)
        {
            if (!_factoryInitialized)
            {
                return;
            }

            ContextFactory.Instance.Cleanup();
            _factoryInitialized = false;
        }
    }
}
