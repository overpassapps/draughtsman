using System.Threading.Channels;

namespace Draughtsman.Server.Auth.PasswordReset;

/// <summary>What a "Forgot password?" request leaves for the worker: the address as typed (trimmed) and where it came from.</summary>
public sealed record ResetMailRequest(string Email, string? SourceIp);

/// <summary>
/// The hand-off between the request and the mail. The request endpoint does the same small amount of work whether or not an
/// account has the address it was given (throttle, one security-event row, enqueue) and answers; looking the address up, making
/// the link and talking to the mail server happen afterwards, in <see cref="PasswordResetMailWorker"/>. That is what makes the
/// response, in content <em>and</em> in time, identical for a known and an unknown address: nothing that differs between them
/// happens while the caller waits. Bounded, so a flood is dropped at the door instead of growing memory (the throttle has
/// already limited each address).
/// </summary>
public sealed class PasswordResetMailQueue
{
    private const int Capacity = 256;

    private readonly Channel<ResetMailRequest> _channel = Channel.CreateBounded<ResetMailRequest>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private int _pending;

    public ChannelReader<ResetMailRequest> Reader => _channel.Reader;

    /// <summary>False when the queue is full; the caller answers exactly as it would have, since the answer never depended on the outcome.</summary>
    public bool TryEnqueue(ResetMailRequest request)
    {
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(request))
        {
            return true;
        }

        Interlocked.Decrement(ref _pending);
        return false;
    }

    internal void Completed()
    {
        Interlocked.Decrement(ref _pending);
    }

    /// <summary>Waits until every request queued so far has been handled. For tests and for shutdown; the request path never calls it.</summary>
    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + timeout;
        while (Volatile.Read(ref _pending) > 0)
        {
            if (DateTimeOffset.UtcNow > until)
            {
                throw new TimeoutException("The password-reset mail queue did not drain.");
            }

            await Task.Delay(10);
        }
    }
}
