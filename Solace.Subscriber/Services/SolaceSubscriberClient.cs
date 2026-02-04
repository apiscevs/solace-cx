using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Solace.Shared;
using Solace.Shared.Messaging;
using SolaceSystems.Solclient.Messaging;
using SolaceFlow = SolaceSystems.Solclient.Messaging.IFlow;
using SolaceSession = SolaceSystems.Solclient.Messaging.ISession;

namespace Solace.Subscriber.Services;

public sealed class SolaceSubscriberClient(
    IOptions<SolaceOptions> options,
    MessageHistory history,
    ILogger<SolaceSubscriberClient> logger) : IHostedService, IDisposable, ISolaceSubscriberClient
{
    public const string ActivitySourceName = "Solace.Subscriber.Messaging";
    private static readonly ActivitySource MessagingActivity = new(ActivitySourceName);

    private static readonly object FactorySync = new();
    private static bool _factoryInitialized;

    private readonly object _sessionSync = new();
    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private readonly string _clientName = $"solace-subscriber-{Environment.ProcessId}";
    private IContext? _context;
    private SolaceSession? _session;
    private SolaceFlow? _flow;
    private ConnectionSnapshot _connection = new(false, "Not Connected", "Ready. Use Connect to establish the session.");
    private bool _isSubscribed;
    private bool _disposed;
    private SubscriberReceiveMode _activeReceiveMode = SubscriberReceiveMode.DirectTopic;
    private string? _activeQueueName;

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

    public string ClientName => _clientName;

    public string SubscriptionTopic => Options.DefaultSubscriptionTopic;

    public SubscriberReceiveMode ActiveReceiveMode => _activeReceiveMode;

    public string? ActiveQueueName => _activeQueueName;

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

    public async Task<bool> ConnectAsync(SubscriberReceiveMode mode, string? queueName, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<bool>(cancellationToken);
        }

        var normalizedQueueName = NormalizeQueueName(queueName);
        if (mode == SubscriberReceiveMode.DurableQueue && string.IsNullOrWhiteSpace(normalizedQueueName))
        {
            UpdateConnection(false, "Configuration Error", "Select a durable queue before connecting.");
            return false;
        }

        using var activity = MessagingActivity.StartActivity("solace.subscriber.connect", ActivityKind.Client);
        activity?.SetTag("messaging.system", "solace");
        activity?.SetTag("messaging.operation", "connect");
        activity?.SetTag("server.address", Options.Host);
        activity?.SetTag("messaging.destination.kind", mode == SubscriberReceiveMode.DirectTopic ? "topic" : "queue");
        activity?.SetTag("messaging.destination.name", mode == SubscriberReceiveMode.DirectTopic ? SubscriptionTopic : normalizedQueueName);

        await _connectionGate.WaitAsync(cancellationToken);

        try
        {
            if (_disposed)
            {
                return false;
            }

            var sameBinding = false;
            lock (_sessionSync)
            {
                if (_session is not null)
                {
                    sameBinding = _activeReceiveMode == mode
                        && (mode == SubscriberReceiveMode.DirectTopic
                            || string.Equals(_activeQueueName, normalizedQueueName, StringComparison.Ordinal));
                }
            }

            if (sameBinding)
            {
                UpdateConnection(true, "Connected", "Session is already active.");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return true;
            }

            if (_session is not null)
            {
                DisconnectSessionAndFlow(gracefulDisconnect: true);
            }

            _context ??= ContextFactory.Instance.CreateContext(new ContextProperties(), null);
            UpdateConnection(false, "Connecting", $"Trying {Options.Host}");

            SolaceSession? newSession = null;
            try
            {
                newSession = _context.CreateSession(BuildSessionProperties(), OnDirectMessageReceived, OnSessionEvent);
                newSession.Connect();

                lock (_sessionSync)
                {
                    _session = newSession;
                    _activeReceiveMode = mode;
                    _activeQueueName = mode == SubscriberReceiveMode.DurableQueue ? normalizedQueueName : null;
                }

                _isSubscribed = false;

                if (mode == SubscriberReceiveMode.DirectTopic)
                {
                    SubscribeToDefaultTopic();
                }
                else if (!BindQueueFlow(normalizedQueueName!))
                {
                    throw new InvalidOperationException("Queue flow could not be established.");
                }

                var destinationSummary = mode == SubscriberReceiveMode.DirectTopic
                    ? $"topic {SubscriptionTopic}"
                    : $"queue {normalizedQueueName}";
                UpdateConnection(true, "Connected", $"Session established at {Options.Host} using {destinationSummary}");
                history.Add(new MessageRecord(
                    DateTimeOffset.UtcNow,
                    MessageDirection.System,
                    "system/subscriber",
                    "Connection established.",
                    true,
                    $"{destinationSummary} / client: {_clientName}"));

                activity?.SetStatus(ActivityStatusCode.Ok);
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

            var (success, detail) = DisconnectSessionAndFlow(gracefulDisconnect: true);
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

            var (success, detail) = DisconnectSessionAndFlow(gracefulDisconnect: false);
            UpdateConnection(false, success ? "Disconnected" : "Disconnect Error", detail);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Simulated unexpected connection loss.",
                success,
                detail));

            return success;
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

        DisconnectSessionAndFlow(gracefulDisconnect: true);

        IContext? context;
        lock (_sessionSync)
        {
            context = _context;
            _context = null;
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

    private bool BindQueueFlow(string queueName)
    {
        using var activity = MessagingActivity.StartActivity("solace.subscriber.bind_queue_flow", ActivityKind.Client);
        activity?.SetTag("messaging.system", "solace");
        activity?.SetTag("messaging.operation", "bind");
        activity?.SetTag("messaging.destination.kind", "queue");
        activity?.SetTag("messaging.destination.name", queueName);
        activity?.SetTag("server.address", Options.Host);

        try
        {
            SolaceSession? session;
            lock (_sessionSync)
            {
                session = _session;
            }

            if (session is null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Session is not available.");
                return false;
            }

            DisposeFlow();

            var flowProperties = new FlowProperties
            {
                AckMode = MessageAckMode.ClientAck,
                BindBlocking = true,
                BindRetries = 2,
                MaxReconnectTries = 20,
                ReconnectRetryIntervalMs = 3_000,
                MaxUnackedMessages = 512
            };

            var queue = ContextFactory.Instance.CreateQueue(queueName);
            var flow = session.CreateFlow(flowProperties, queue, null, OnQueueMessageReceived, OnFlowEvent);

            lock (_sessionSync)
            {
                _flow = flow;
            }

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Queue flow bound.",
                true,
                $"{queueName} / ClientAck enabled"));

            activity?.SetStatus(ActivityStatusCode.Ok);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to bind queue flow for {QueueName}", queueName);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Queue flow bind failed.",
                false,
                ex.Message));

            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return false;
        }
    }

    private void OnDirectMessageReceived(object? sender, MessageEventArgs args)
    {
        var topic = args.Message.Destination?.Name ?? "(no topic)";
        using var activity = MessagingActivity.StartActivity("solace.subscriber.receive.direct", ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "solace");
        activity?.SetTag("messaging.operation", "receive");
        activity?.SetTag("messaging.destination.kind", "topic");
        activity?.SetTag("messaging.destination.name", topic);
        activity?.SetTag("server.address", Options.Host);

        try
        {
            var payload = ExtractPayload(args.Message);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.Inbound,
                topic,
                payload,
                true,
                "Received (Direct)"));

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process inbound direct message.");

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Inbound direct message processing failed.",
                false,
                ex.Message));

            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
        finally
        {
            args.Message.Dispose();
        }
    }

    private void OnQueueMessageReceived(object? sender, MessageEventArgs args)
    {
        var sourceFlow = sender as SolaceFlow;
        var destination = args.Message.Destination?.Name ?? _activeQueueName ?? "(queue)";
        using var activity = MessagingActivity.StartActivity("solace.subscriber.receive.queue", ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "solace");
        activity?.SetTag("messaging.operation", "receive");
        activity?.SetTag("messaging.destination.kind", "queue");
        activity?.SetTag("messaging.destination.name", _activeQueueName ?? destination);
        activity?.SetTag("server.address", Options.Host);

        try
        {
            var payload = ExtractPayload(args.Message);
            var messageId = args.Message.ADMessageId;
            var ackResult = sourceFlow?.Ack(messageId) ?? ReturnCode.SOLCLIENT_FAIL;
            var ackSuccess = ackResult == ReturnCode.SOLCLIENT_OK;
            var details = ackSuccess
                ? $"Received + ACK (ADMessageId={messageId})"
                : $"ACK failed ({ackResult}, ADMessageId={messageId})";

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.Inbound,
                $"queue://{_activeQueueName ?? destination}",
                payload,
                ackSuccess,
                details));

            if (!ackSuccess)
            {
                logger.LogWarning("Queue ACK returned {AckResult} for message id {MessageId}", ackResult, messageId);
                activity?.SetStatus(ActivityStatusCode.Error, ackResult.ToString());
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process inbound queue message.");

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/subscriber",
                "Inbound queue message processing failed.",
                false,
                ex.Message));

            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
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
                if (_activeReceiveMode == SubscriberReceiveMode.DirectTopic)
                {
                    SubscribeToDefaultTopic();
                }
                break;
            case SessionEvent.Reconnecting:
                UpdateConnection(false, "Reconnecting", string.IsNullOrWhiteSpace(args.Info) ? "Broker reconnect in progress." : args.Info);
                break;
            case SessionEvent.ConnectFailedError:
            case SessionEvent.DownError:
                _isSubscribed = false;
                lock (_sessionSync)
                {
                    _flow = null;
                }
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

    private void OnFlowEvent(object? sender, FlowEventArgs args)
    {
        switch (args.Event)
        {
            case FlowEvent.UpNotice:
            case FlowEvent.FlowActive:
            case FlowEvent.Reconnected:
                UpdateConnection(true, "Connected", $"Queue flow active on {_activeQueueName}.");
                break;
            case FlowEvent.Reconnecting:
                UpdateConnection(false, "Reconnecting", $"Queue flow reconnecting on {_activeQueueName}.");
                break;
            case FlowEvent.DownError:
            case FlowEvent.ParentSessionDown:
            case FlowEvent.BindFailedError:
                UpdateConnection(false, "Disconnected", string.IsNullOrWhiteSpace(args.Info) ? "Queue flow is down." : args.Info);
                break;
        }
    }

    private void SubscribeToDefaultTopic()
    {
        if (_isSubscribed || _activeReceiveMode != SubscriberReceiveMode.DirectTopic)
        {
            return;
        }

        using var activity = MessagingActivity.StartActivity("solace.subscriber.subscribe", ActivityKind.Client);
        activity?.SetTag("messaging.system", "solace");
        activity?.SetTag("messaging.operation", "subscribe");
        activity?.SetTag("messaging.destination.kind", "topic");
        activity?.SetTag("messaging.destination.name", SubscriptionTopic);
        activity?.SetTag("server.address", Options.Host);

        try
        {
            SolaceSession? session;
            lock (_sessionSync)
            {
                session = _session;
            }

            if (session is null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Session is not available.");
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

            activity?.SetStatus(ActivityStatusCode.Ok);
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

            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
    }

    private (bool Success, string Detail) DisconnectSessionAndFlow(bool gracefulDisconnect)
    {
        SolaceFlow? flow;
        SolaceSession? session;

        lock (_sessionSync)
        {
            flow = _flow;
            session = _session;
            _flow = null;
            _session = null;
        }

        _isSubscribed = false;

        if (flow is not null)
        {
            try
            {
                flow.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Disposing subscriber flow raised an exception.");
            }
        }

        if (session is null)
        {
            return (true, "Session is already closed.");
        }

        var success = true;
        var detail = gracefulDisconnect ? "Session closed by user." : "Session disposed without graceful disconnect.";

        if (gracefulDisconnect)
        {
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
        }

        session.Dispose();
        return (success, detail);
    }

    private void DisposeFlow()
    {
        SolaceFlow? flow;
        lock (_sessionSync)
        {
            flow = _flow;
            _flow = null;
        }

        if (flow is not null)
        {
            try
            {
                flow.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Disposing subscriber flow raised an exception.");
            }
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

    private static string? NormalizeQueueName(string? queueName)
    {
        var cleaned = queueName?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
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
