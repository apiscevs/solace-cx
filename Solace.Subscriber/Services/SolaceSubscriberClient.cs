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
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private IContext? _context;
    private SolaceSession? _session;
    private ConnectionSnapshot _connection = new(false, "Not Connected", "Ready. Use Connect to establish the session.");
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
            UpdateConnection(false, "Not Connected", "Ready. Use Connect to establish the session.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subscriber failed to initialize Solace context.");
            UpdateConnection(false, "Connection Error", ex.Message);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Subscriber context failed to start.",
                false,
                ex.Message));
        }

        return Task.CompletedTask;
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<bool>(cancellationToken);
        }

        await _connectionGate.WaitAsync(cancellationToken);

        try
        {
            if (_disposed)
            {
                return false;
            }

            lock (_sessionSync)
            {
                if (_session is not null)
                {
                    UpdateConnection(true, "Connected", "Session is already active.");
                    return true;
                }
            }

            _context ??= ContextFactory.Instance.CreateContext(new ContextProperties(), null);
            UpdateConnection(false, "Connecting", $"Trying {Options.Host}");

            SolaceSession? newSession = null;
            try
            {
                newSession = _context.CreateSession(BuildSessionProperties(), OnMessageReceived, OnSessionEvent);
                newSession.Connect();

                lock (_sessionSync)
                {
                    _session = newSession;
                }

                _isSubscribed = false;
                SubscribeToDefaultTopic();

                UpdateConnection(true, "Connected", $"Session established at {Options.Host}");
                history.Add(new MessageRecord(
                    DateTimeOffset.UtcNow,
                    MessageDirection.System,
                    "system/subscriber",
                    "Connection established.",
                    true,
                    Options.Host));

                return true;
            }
            catch (Exception ex)
            {
                newSession?.Dispose();

                logger.LogError(ex, "Subscriber failed to connect Solace session.");
                UpdateConnection(false, "Connection Error", ex.Message);

                history.Add(new MessageRecord(
                    DateTimeOffset.UtcNow,
                    MessageDirection.System,
                    "system/subscriber",
                    "Connection attempt failed.",
                    false,
                    ex.Message));

                return false;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<bool>(cancellationToken);
        }

        await _connectionGate.WaitAsync(cancellationToken);

        try
        {
            if (_disposed)
            {
                return false;
            }

            SolaceSession? session;
            lock (_sessionSync)
            {
                session = _session;
                _session = null;
            }

            _isSubscribed = false;

            if (session is null)
            {
                UpdateConnection(false, "Not Connected", "Session is already closed.");
                return true;
            }

            var success = true;
            var detail = "Session closed by user.";

            try
            {
                session.Disconnect();
            }
            catch (Exception ex)
            {
                success = false;
                detail = ex.Message;
                logger.LogWarning(ex, "Subscriber disconnect raised an exception.");
            }
            finally
            {
                session.Dispose();
            }

            UpdateConnection(false, success ? "Not Connected" : "Disconnect Error", detail);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Session disconnected.",
                success,
                detail));

            return success;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<bool> SimulateConnectionLossAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<bool>(cancellationToken);
        }

        await _connectionGate.WaitAsync(cancellationToken);

        try
        {
            if (_disposed)
            {
                return false;
            }

            SolaceSession? session;
            lock (_sessionSync)
            {
                session = _session;
                _session = null;
            }

            _isSubscribed = false;

            if (session is null)
            {
                UpdateConnection(false, "Not Connected", "No active session to drop.");
                return true;
            }

            // Dispose the session directly to mimic a sudden connection drop.
            session.Dispose();
            UpdateConnection(false, "Disconnected", "Simulated unexpected connection loss.");

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Simulated unexpected connection loss.",
                true,
                "Session disposed without graceful disconnect."));

            return true;
        }
        finally
        {
            _connectionGate.Release();
        }
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
        UpdateConnection(false, "Not Connected", "Subscriber service stopped.");

        _connectionGate.Dispose();
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
                UpdateConnection(true, "Connected", string.IsNullOrWhiteSpace(args.Info) ? "Session is active." : args.Info);
                SubscribeToDefaultTopic();
                break;
            case SessionEvent.Reconnecting:
                UpdateConnection(false, "Reconnecting", string.IsNullOrWhiteSpace(args.Info) ? "Broker reconnect in progress." : args.Info);
                break;
            case SessionEvent.ConnectFailedError:
            case SessionEvent.DownError:
                _isSubscribed = false;
                UpdateConnection(false, "Disconnected", string.IsNullOrWhiteSpace(args.Info) ? "Session is down." : args.Info);
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
