namespace Solace.Shared.Messaging;

public sealed class MessageHistory
{
    private const int MaxMessages = 200;
    private readonly object _sync = new();
    private readonly List<MessageRecord> _messages = [];

    public event Action? Changed;

    public IReadOnlyList<MessageRecord> Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _messages.ToList();
            }
        }
    }

    public void Add(MessageRecord message)
    {
        lock (_sync)
        {
            _messages.Insert(0, message);
            if (_messages.Count > MaxMessages)
            {
                _messages.RemoveRange(MaxMessages, _messages.Count - MaxMessages);
            }
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_sync)
        {
            _messages.Clear();
        }

        Changed?.Invoke();
    }
}
