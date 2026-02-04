using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Solace.Shared;
using Solace.Shared.Messaging;
using SolaceSystems.Solclient.Messaging;
using SolaceSession = SolaceSystems.Solclient.Messaging.ISession;

namespace Solace.Publisher.Services;

public sealed class SolacePublisherClient(
    IOptions<SolaceOptions> options,
    MessageHistory history,
    ILogger<SolacePublisherClient> logger) : IHostedService, IDisposable, ISolacePublisherClient
{
    public const string ActivitySourceName = "Solace.Publisher.Messaging";
    private static readonly ActivitySource MessagingActivity = new(ActivitySourceName);

    private static readonly object FactorySync = new();
    private static bool _factoryInitialized;

    private readonly object _sessionSync = new();
    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private IContext? _context;
    private SolaceSession? _session;
    private ConnectionSnapshot _connection = new(false, "Not Connected", "Ready. Use Connect to establish the session.");
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
            logger.LogError(ex, "Publisher failed to initialize Solace context.");
            UpdateConnection(false, "Connection Error", ex.Message);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/publisher",
                "Publisher context failed to start.",
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

        using var activity = MessagingActivity.StartActivity("solace.publisher.connect", ActivityKind.Client);
        activity?.SetTag("messaging.system", "solace");
        activity?.SetTag("messaging.operation", "connect");
        activity?.SetTag("server.address", Options.Host);

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
                    activity?.SetStatus(ActivityStatusCode.Ok);
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

                UpdateConnection(true, "Connected", $"Session established at {Options.Host}");
                history.Add(new MessageRecord(
                    DateTimeOffset.UtcNow,
                    MessageDirection.System,
                    "system/publisher",
                    "Connection established.",
                    true,
                    Options.Host));

                activity?.SetStatus(ActivityStatusCode.Ok);
                return true;
            }
            catch (Exception ex)
            {
                newSession?.Dispose();

                logger.LogError(ex, "Publisher failed to connect Solace session.");
                UpdateConnection(false, "Connection Error", ex.Message);

                history.Add(new MessageRecord(
                    DateTimeOffset.UtcNow,
                    MessageDirection.System,
                    "system/publisher",
                    "Connection attempt failed.",
                    false,
                    ex.Message));

                activity?.SetTag("error.type", ex.GetType().Name);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
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
                logger.LogWarning(ex, "Publisher disconnect raised an exception.");
            }
            finally
            {
                session.Dispose();
            }

            UpdateConnection(false, success ? "Not Connected" : "Disconnect Error", detail);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/publisher",
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

    public Task<bool> PublishDirectAsync(string topic, string payload, CancellationToken cancellationToken = default)
    {
        var resolvedTopic = ResolveTopic(topic);
        return PublishInternalAsync(
            resolvedDestination: resolvedTopic,
            payload: payload,
            isDirectMode: true,
            partitionKey: null,
            cancellationToken: cancellationToken);
    }

    public Task<bool> PublishToQueueAsync(string queueName, string payload, string? partitionKey, CancellationToken cancellationToken = default)
    {
        var resolvedQueue = ResolveQueueName(queueName);
        return PublishInternalAsync(
            resolvedDestination: resolvedQueue,
            payload: payload,
            isDirectMode: false,
            partitionKey: partitionKey,
            cancellationToken: cancellationToken);
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
                "system/publisher",
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
                logger.LogDebug(ex, "Publisher session disconnect raised an exception.");
            }

            session.Dispose();
        }

        context?.Dispose();

        CleanupFactory();
        UpdateConnection(false, "Not Connected", "Publisher service stopped.");

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
            ClientName = "solace-publisher",
            ConnectBlocking = true,
            ConnectRetries = 3,
            ConnectRetriesPerHost = 3,
            ReconnectRetries = 20,
            ReconnectRetriesWaitInMsecs = 3_000,
            ReapplySubscriptions = true,
            GenerateSendTimestamps = true,
            SSLValidateCertificate = false
        };
    }

    private void OnMessageReceived(object? sender, MessageEventArgs args)
    {
        args.Message.Dispose();
    }

    private void OnSessionEvent(object? sender, SessionEventArgs args)
    {
        switch (args.Event)
        {
            case SessionEvent.UpNotice:
            case SessionEvent.Reconnected:
                UpdateConnection(true, "Connected", string.IsNullOrWhiteSpace(args.Info) ? "Session is active." : args.Info);
                break;
            case SessionEvent.Reconnecting:
                UpdateConnection(false, "Reconnecting", string.IsNullOrWhiteSpace(args.Info) ? "Broker reconnect in progress." : args.Info);
                break;
            case SessionEvent.ConnectFailedError:
            case SessionEvent.DownError:
                UpdateConnection(false, "Disconnected", string.IsNullOrWhiteSpace(args.Info) ? "Session is down." : args.Info);
                break;
            case SessionEvent.RejectedMessageError:
                history.Add(new MessageRecord(
                    DateTimeOffset.UtcNow,
                    MessageDirection.System,
                    "system/publisher",
                    "Broker rejected an outbound message.",
                    false,
                    args.Info));
                break;
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

    private string ResolveTopic(string topic)
    {
        var cleaned = topic.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? Options.DefaultPublishTopic : cleaned;
    }

    private string ResolveQueueName(string queueName)
    {
        return queueName.Trim();
    }

    private Task<bool> PublishInternalAsync(
        string resolvedDestination,
        string payload,
        bool isDirectMode,
        string? partitionKey,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(resolvedDestination))
        {
            return Task.FromResult(false);
        }

        using var activity = MessagingActivity.StartActivity("solace.publisher.publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "solace");
        activity?.SetTag("messaging.operation", "publish");
        activity?.SetTag("messaging.destination.name", resolvedDestination);
        activity?.SetTag("messaging.destination.kind", isDirectMode ? "topic" : "queue");
        activity?.SetTag("server.address", Options.Host);

        SolaceSession? session;
        lock (_sessionSync)
        {
            session = _session;
        }

        if (session is null)
        {
            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/publisher",
                "Cannot publish while disconnected.",
                false,
                "Session is not available."));

            activity?.SetStatus(ActivityStatusCode.Error, "Session is not available.");
            return Task.FromResult(false);
        }

        try
        {
            using var message = ContextFactory.Instance.CreateMessage();

            message.Destination = isDirectMode
                ? ContextFactory.Instance.CreateTopic(resolvedDestination)
                : ContextFactory.Instance.CreateQueue(resolvedDestination);
            message.DeliveryMode = isDirectMode ? MessageDeliveryMode.Direct : MessageDeliveryMode.Persistent;
            message.BinaryAttachment = Encoding.UTF8.GetBytes(payload);
            message.SenderTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!isDirectMode && !string.IsNullOrWhiteSpace(partitionKey))
            {
                message.CreateUserPropertyMap();
                message.UserPropertyMap.AddString(MessageUserPropertyConstants.SOLCLIENT_USER_PROP_QUEUE_PARTITION_KEY, partitionKey);
            }

            var result = session.Send(message);
            var success = result == ReturnCode.SOLCLIENT_OK;

            var details = success
                ? isDirectMode
                    ? "Sent (Direct)"
                    : $"Sent (Persistent, partition key: {partitionKey ?? "none"})"
                : result.ToString();

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.Outbound,
                isDirectMode ? resolvedDestination : $"queue://{resolvedDestination}",
                payload,
                success,
                details));

            if (!success)
            {
                logger.LogWarning("Publish returned {Result} for destination {Destination}", result, resolvedDestination);
                activity?.SetStatus(ActivityStatusCode.Error, result.ToString());
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
            }

            return Task.FromResult(success);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publish failed for destination {Destination}", resolvedDestination);
            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.Outbound,
                isDirectMode ? resolvedDestination : $"queue://{resolvedDestination}",
                payload,
                false,
                ex.Message));

            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return Task.FromResult(false);
        }
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
