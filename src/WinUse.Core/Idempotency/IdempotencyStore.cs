using System.Collections.Concurrent;
using System.Text.Json;
using WinUse.Core.Protocol;

namespace WinUse.Core.IdempotencyFlow;

/// <summary>
/// 幂等存储（06 §10）：相同 idempotency_key 在 TTL 内返回首次响应并标记 replayed。
/// 并发同键请求共享首次执行，不重复产生副作用。
/// </summary>
public sealed class IdempotencyStore
{
    private sealed record Entry(DateTime CreatedAt, Task<RpcResponse> Task);

    private readonly ConcurrentDictionary<string, Entry> _store = new();
    private readonly TimeSpan _ttl;

    public IdempotencyStore(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
    }

    public async Task<RpcResponse> GetOrAddAsync(string key, Func<Task<RpcResponse>> produceAsync)
    {
        PurgeExpired();
        var tcs = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = _store.GetOrAdd(key, _ => new Entry(DateTime.UtcNow, RunAndComplete(tcs, produceAsync)));

        var first = !entry.Task.Equals(tcs.Task) ? await entry.Task.ConfigureAwait(false) : await tcs.Task.ConfigureAwait(false);

        // 首个请求原样返回；后续（重放）请求标记 replayed:true。
        if (entry.CreatedAt != default && !ReferenceEquals(entry.Task, tcs.Task))
        {
            return ReplayMarker.Mark(first);
        }
        return first;
    }

    private static async Task<RpcResponse> RunAndComplete(
        TaskCompletionSource<RpcResponse> tcs, Func<Task<RpcResponse>> produceAsync)
    {
        try
        {
            var response = await produceAsync().ConfigureAwait(false);
            tcs.SetResult(response);
            return response;
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
            throw;
        }
    }

    private void PurgeExpired()
    {
        var cutoff = DateTime.UtcNow - _ttl;
        foreach (var (key, entry) in _store)
        {
            if (entry.CreatedAt < cutoff)
            {
                _store.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>在响应结果中合并 replayed:true（06 §10.2）。</summary>
internal static class ReplayMarker
{
    public static RpcResponse Mark(RpcResponse original)
    {
        if (original.Result is null)
        {
            return new RpcResponse { Id = original.Id, Result = new Dictionary<string, object?> { ["replayed"] = true }, Error = original.Error };
        }

        var element = JsonSerializer.SerializeToElement(original.Result);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var dict = element.Deserialize<Dictionary<string, JsonElement>>() ?? new();
            var merged = new Dictionary<string, object?> { ["replayed"] = true };
            foreach (var (k, v) in dict)
            {
                merged[k] = v;
            }
            return new RpcResponse { Id = original.Id, Result = merged, Error = original.Error };
        }

        return new RpcResponse
        {
            Id = original.Id,
            Result = new Dictionary<string, object?> { ["replayed"] = true, ["value"] = element },
            Error = original.Error,
        };
    }
}
