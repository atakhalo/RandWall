using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace RandWall.Services;

/// <summary>
/// 专用 STA 线程执行器。Shell 的 COM 对象在其中创建与调用，避免跨公寓问题和 UI 卡顿。
/// </summary>
internal sealed class StaExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private volatile bool _disposed;

    public StaExecutor()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "RandWall.STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public T Invoke<T>(Func<T> func)
    {
        if (Thread.CurrentThread == _thread)
        {
            return func();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(StaExecutor));
        }

        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action action) => Invoke(() =>
    {
        action();
        return true;
    });

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error("STA 线程执行任务失败", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
    }
}
