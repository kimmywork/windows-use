using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinUse.Platform.Windows.Uia;

/// <summary>
/// UIA 隔离执行器（05 §10.3、06 §5.4、PoC §7 C8）。
/// 每次调用在独立 MTA 线程上创建并消费 UIA COM 对象；硬超时后**放弃线程**，
/// 主进程绝不被挂死的目标拖挂。并发达上限直接拒绝服务。
/// </summary>
public sealed class UiaIsolationRunner
{
    private readonly int _maxConcurrent;
    private int _active;
    private int _abandoned;
    private long _totalCalls;

    public UiaIsolationRunner(int maxConcurrent = 8)
    {
        _maxConcurrent = maxConcurrent;
    }

    public int ActiveCount => Volatile.Read(ref _active);
    public int AbandonedCount => Volatile.Read(ref _abandoned);
    public long TotalCalls => Volatile.Read(ref _totalCalls);

    /// <summary>在隔离 MTA 线程上执行 UIA 操作。超时抛出 <see cref="UiaTimeoutException"/>。</summary>
    internal Task<T> RunAsync<T>(Func<IUIAutomation, T> work, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _active) >= _maxConcurrent)
        {
            throw new UiaBusyException(_maxConcurrent);
        }

        Interlocked.Increment(ref _totalCalls);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            IUIAutomation? automation = null;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("e22ad333-b25f-460c-83d8-29994c139781"))
                           ?? throw new InvalidOperationException("无法定位 CUIAutomation8 CLSID");
                automation = (IUIAutomation)Activator.CreateInstance(type)!;
                var result = work(automation);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (automation is not null)
                {
                    Marshal.ReleaseComObject(automation);
                }
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.MTA);
        Interlocked.Increment(ref _active);

        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        thread.Start();

        try
        {
            if (thread.Join((int)Math.Min(int.MaxValue, Math.Max(1, timeout.TotalMilliseconds))))
            {
                return tcs.Task;
            }

            // 硬超时：线程可能正阻塞在挂死目标的 COM 调用上 —— 放弃它。
            Interlocked.Increment(ref _abandoned);
            tcs.TrySetException(new UiaTimeoutException(timeout));
            return tcs.Task;
        }
        finally
        {
            registration.Dispose();
            Interlocked.Decrement(ref _active);
        }
    }
}

public sealed class UiaTimeoutException : Exception
{
    public UiaTimeoutException(TimeSpan timeout)
        : base($"UIA 调用超过硬超时 {timeout.TotalMilliseconds:0}ms，已放弃隔离线程")
    {
    }
}

public sealed class UiaBusyException : Exception
{
    public UiaBusyException(int max)
        : base($"UIA 并发已达上限 {max}，拒绝服务（防止挂死目标耗尽线程）")
    {
    }
}
