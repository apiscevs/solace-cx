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
    private static readonly object FactorySync = new();
    private static bool _factoryInitialized;

    private readonly object _sessionSync = new();
    private readonly object _stateSync = new();

    private IContext? _context;
    private SolaceSession? _session;
    private ConnectionSnapshot _connection = new(false, "Not Connected", "Waiting to establish Solace session.");
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
            _session = _context.CreateSession(BuildSessionProperties(), OnMessageReceived, OnSessionEvent);
            _session.Connect();

            UpdateConnection(true, "Connected", $"Session established at {Options.Host}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publisher failed to initialize Solace session.");
            UpdateConnection(false, "Connection Error", ex.Message);

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.System,
                "system/publisher",
                "Publisher session failed to start.",
                false,
                ex.Message));
        }

        return Task.CompletedTask;
    }

    public Task<bool> PublishAsync(string topic, string payload, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

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

            return Task.FromResult(false);
        }

        try
        {
            var resolvedTopic = ResolveTopic(topic);
            using var message = ContextFactory.Instance.CreateMessage();

            message.Destination = ContextFactory.Instance.CreateTopic(resolvedTopic);
            message.DeliveryMode = MessageDeliveryMode.Direct;
            message.BinaryAttachment = Encoding.UTF8.GetBytes(payload);
            message.SenderTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var result = session.Send(message);
            var success = result == ReturnCode.SOLCLIENT_OK;

            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.Outbound,
                resolvedTopic,
                payload,
                success,
                success ? "Sent" : result.ToString()));

            if (!success)
            {
                logger.LogWarning("Publish returned {Result} for topic {Topic}", result, resolvedTopic);
            }

            return Task.FromResult(success);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publish failed for topic {Topic}", topic);
            history.Add(new MessageRecord(
                DateTimeOffset.UtcNow,
                MessageDirection.Outbound,
                ResolveTopic(topic),
                payload,
                false,
                ex.Message));

            return Task.FromResult(false);
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
        UpdateConnection(false, "Not Connected", "Publisher session has stopped.");

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
                UpdateConnection(true, "Connected", args.Info);
                break;
            case SessionEvent.Reconnecting:
                UpdateConnection(false, "Reconnecting", args.Info);
                break;
            case SessionEvent.ConnectFailedError:
            case SessionEvent.DownError:
                UpdateConnection(false, "Disconnected", args.Info);
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
