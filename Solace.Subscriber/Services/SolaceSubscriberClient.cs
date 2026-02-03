using System.Text;
using Microsoft.Extensions.Options;
using Solace.Shared;
using Solace.Shared.Messaging;
using SolaceSystems.Solclient.Messaging;
using SolaceSession = SolaceSystems.Solclient.Messaging.ISession;

namespace Solace.Subscriber.Services;

public sealed class SolaceSubscriberClient(
    IOptions<SolaceOptions> options,
    MessageHistory history,
    ILogger<SolaceSubscriberClient> logger) : IHostedService, IDisposable, ISolaceSubscriberClient
{
    private static readonly object FactorySync = new();
    private static bool _factoryInitialized;

    private readonly object _sessionSync = new();
    private readonly object _stateSync = new();

    private IContext? _context;
    private SolaceSession? _session;
    private ConnectionSnapshot _connection = new(false, "Not Connected", "Waiting to connect to Solace.");
    private bool _isSubscribed;
    private bool _disposed;

    public event Action? ConnectionChanged;

    public SolaceOptions Options { get; } = options.Value;

    public ConnectionSnapshot Connection
    {
        get
        {
            lock (_stateSync)
            {
                return _connection;
            }
        }
    }

    public string SubscriptionTopic => Options.DefaultSubscriptionTopic;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureFactoryInitialized();

        try
        {
            _context = ContextFactory.Instance.CreateContext(new ContextProperties(), null);
            _session = _context.CreateSession(BuildSessionProperties(), OnMessageReceived, OnSessionEvent);
            _session.Connect();
            UpdateConnection(true, "Connected", $"Session established at {Options.Host}");
            SubscribeToDefaultTopic();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subscriber failed to initialize Solace session.");
            UpdateConnection(false, "Connection Error", ex.Message);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Subscriber session failed to start.",
                false,
                ex.Message));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SolaceSession? session;
        IContext? context;

        lock (_sessionSync)
        {
            session = _session;
            context = _context;
            _session = null;
            _context = null;
        }

        if (session is not null)
        {
            try
            {
                session.Disconnect();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Subscriber session disconnect raised an exception.");
            }

            session.Dispose();
        }

        context?.Dispose();

        CleanupFactory();
        UpdateConnection(false, "Not Connected", "Subscriber session has stopped.");

        _disposed = true;
    }

    private SessionProperties BuildSessionProperties()
    {
        return new SessionProperties
        {
            Host = Options.Host,
            VPNName = Options.VpnName,
            UserName = Options.Username,
            Password = Options.Password,
            ClientName = "solace-subscriber",
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

    private void OnMessageReceived(object? sender, MessageEventArgs args)
    {
        try
        {
            var message = args.Message;
            var topic = message.Destination?.Name ?? "(no topic)";
            var payload = ExtractPayload(message);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.Inbound,
                topic,
                payload,
                true,
                "Received"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process inbound message.");

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Inbound message processing failed.",
                false,
                ex.Message));
        }
        finally
        {
            args.Message.Dispose();
        }
    }

    private void OnSessionEvent(object? sender, SessionEventArgs args)
    {
        switch (args.Event)
        {
            case SessionEvent.UpNotice:
            case SessionEvent.Reconnected:
                UpdateConnection(true, "Connected", args.Info);
                SubscribeToDefaultTopic();
                break;
            case SessionEvent.Reconnecting:
                UpdateConnection(false, "Reconnecting", args.Info);
                break;
            case SessionEvent.ConnectFailedError:
            case SessionEvent.DownError:
                _isSubscribed = false;
                UpdateConnection(false, "Disconnected", args.Info);
                break;
            case SessionEvent.SubscriptionOk:
                history.Add(new MessageRecord(
                    DateTimeOffset.UtcNow,
                    MessageDirection.System,
                    "system/subscriber",
                    "Subscription active.",
                    true,
                    SubscriptionTopic));
                break;
            case SessionEvent.SubscriptionError:
                history.Add(new MessageRecord(
                    DateTimeOffset.UtcNow,
                    MessageDirection.System,
                    "system/subscriber",
                    "Subscription error.",
                    false,
                    args.Info));
                break;
        }
    }

    private void SubscribeToDefaultTopic()
    {
        if (_isSubscribed)
        {
            return;
        }

        try
        {
            SolaceSession? session;
            lock (_sessionSync)
            {
                session = _session;
            }

            if (session is null)
            {
                return;
            }

            session.Subscribe(ContextFactory.Instance.CreateTopic(SubscriptionTopic), false);
            _isSubscribed = true;

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Subscribed to topic.",
                true,
                SubscriptionTopic));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to subscribe to {Topic}", SubscriptionTopic);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Subscription failed.",
                false,
                ex.Message));
        }
    }

    private void UpdateConnection(bool isConnected, string status, string? details)
    {
        lock (_stateSync)
        {
            _connection = new ConnectionSnapshot(isConnected, status, details ?? string.Empty);
        }

        ConnectionChanged?.Invoke();
    }

    private static string ExtractPayload(IMessage message)
    {
        if (message.BinaryAttachment is { Length: > 0 } bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        if (message.XmlContent is { Length: > 0 } xmlBytes)
        {
            return Encoding.UTF8.GetString(xmlBytes);
        }

        return "(empty payload)";
    }

    private static void EnsureFactoryInitialized()
    {
        lock (FactorySync)
        {
            if (_factoryInitialized)
            {
                return;
            }

            ContextFactory.Instance.Init(new ContextFactoryProperties());
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
