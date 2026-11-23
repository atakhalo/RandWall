using System;
using System.Threading;

namespace RandWall.Services;

/// <summary>单实例守卫：用命名 Mutex 保证只跑一个进程，命名事件用于唤起已有窗口。</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "RandWall.SingleInstance.1A7C2E5B";
    private const string EventName = "RandWall.ShowWindow.1A7C2E5B";

    private Mutex? _mutex;
    private EventWaitHandle? _event;
    private RegisteredWaitHandle? _waitHandle;

    /// <summary>尝试成为唯一实例。</summary>
    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
            {
                return true;
            }

            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"单实例检查失败：{ex.Message}");
            return true;
        }
    }

    /// <summary>通知正在运行的实例显示窗口。</summary>
    public void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(EventName);
                handle.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(120);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"唤起已有实例失败：{ex.Message}");
                return;
            }
        }
    }

    /// <summary>作为唯一实例时，监听唤起请求。</summary>
    public void ListenForActivation(Action onActivate)
    {
        try
        {
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            _waitHandle = ThreadPool.RegisterWaitForSingleObject(
                _event,
                (_, timedOut) =>
                {
                    if (!timedOut)
                    {
                        onActivate();
                    }
                },
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"注册唤起监听失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _waitHandle?.Unregister(null);
        _event?.Dispose();

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // 未被当前线程持有
            }

            _mutex.Dispose();
        }
    }
}
