using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FreeTierMail;

/// <summary>
/// Remembers the result of each idempotency key for a while. Concurrent sends with one key share
/// one attempt. Only <see cref="SendStatus.Sent"/> and <see cref="SendStatus.Unknown"/> results are
/// kept: after <see cref="SendStatus.Failed"/> nothing went out, so a retry may try again.
/// </summary>
internal sealed class IdempotencyCache(int capacity, TimeSpan retention, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public async Task<SendResult> GetOrSendAsync(string key, Func<Task<SendResult>> send)
    {
        TaskCompletionSource<SendResult>? mine = null;
        Task<SendResult> shared;
        lock (_lock)
        {
            var now = time.GetUtcNow();
            if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
            {
                shared = entry.Result.Task;
            }
            else
            {
                mine = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                shared = mine.Task;
                _entries[key] = new Entry(mine, now + retention);
                _order.Enqueue(key);
                Trim(now);
            }
        }

        if (mine is null)
        {
            return await shared.ConfigureAwait(false) with { IsReplay = true };
        }

        // The send runs outside the lock: a provider must never run while other keys wait.
        try
        {
            var result = await send().ConfigureAwait(false);
            if (result.Status == SendStatus.Failed)
            {
                Forget(key, mine);
            }

            mine.SetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            Forget(key, mine);
            mine.SetException(ex);
            throw;
        }
    }

    private void Forget(string key, TaskCompletionSource<SendResult> source)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Result == source)
            {
                _entries.Remove(key);
            }
        }
    }

    // Drops expired entries from the front, then the oldest while over capacity. A key sent again
    // after expiring is queued twice; the stale queue item finds a newer entry and leaves it.
    private void Trim(DateTimeOffset now)
    {
        while (_order.TryPeek(out var oldest))
        {
            var present = _entries.TryGetValue(oldest, out var entry);
            if (present && entry!.ExpiresAt > now && _entries.Count <= capacity)
            {
                break;
            }

            _order.Dequeue();
            if (present && (entry!.ExpiresAt <= now || _entries.Count > capacity))
            {
                _entries.Remove(oldest);
            }
        }
    }

    private sealed record Entry(TaskCompletionSource<SendResult> Result, DateTimeOffset ExpiresAt);
}
