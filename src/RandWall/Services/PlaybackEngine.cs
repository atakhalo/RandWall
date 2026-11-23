using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using RandWall.Models;

namespace RandWall.Services;

/// <summary>
/// 轮换引擎：维护壁纸池游标、随机袋、历史记录与定时器。
/// 所有成员应在 UI 线程调用，事件也在 UI 线程触发。
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private const int MaxHistory = 64;

    private readonly DesktopWallpaperService _wallpaper;
    private readonly WallpaperLibrary _library;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly List<WallpaperGroup> _history = new();
    private readonly Random _random = new();
    private readonly List<string> _bag = new();

    private int _bagPos;
    private int _cursor;
    private DateTimeOffset _nextDue;
    private TimeSpan _pausedRemaining = TimeSpan.Zero;
    private bool _paused;
    private bool _disposed;
    private string? _lastError;
    private string? _hint;
    private string? _lastPickedPath;
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private string[] _displayedPaths = Array.Empty<string>();

    private WallpaperGroup? _nextPreview;
    private bool _nextPreviewValid;
    private (int Cursor, int BagPos, string[] Bag) _nextPreviewAfterState;

    public PlaybackEngine(DesktopWallpaperService wallpaper, WallpaperLibrary library, AppSettings settings)
    {
        _wallpaper = wallpaper;
        _library = library;
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>状态（暂停、错误、历史等）变化。</summary>
    public event EventHandler? Changed;

    /// <summary>每秒一次的计时脉冲，用于刷新倒计时。</summary>
    public event EventHandler? Tick;

    /// <summary>新的一组壁纸已经确定（随后会分帧应用）。</summary>
    public event EventHandler<WallpaperGroup>? GroupApplied;

    public WallpaperLibrary Library => _library;

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    /// <summary>每块显示器当前（或正在应用）的壁纸路径。</summary>
    public IReadOnlyList<string> DisplayedPaths => _displayedPaths;

    public WallpaperGroup? CurrentGroup => _history.Count > 0 ? _history[^1] : null;

    public WallpaperGroup? PreviousGroup => _history.Count >= 2 ? _history[^2] : null;

    public IReadOnlyList<WallpaperGroup> History => _history;

    public bool IsPaused => _paused;

    public bool HasContent => _library.Files.Count > 0;

    public int PoolCount => _library.Files.Count;

    public int Cursor => _cursor;

    public string? LastError => _lastError;

    /// <summary>引导性提示（例如还没有添加文件夹），不是错误。</summary>
    public string? Hint => _hint;

    public TimeSpan Interval => TimeSpan.FromSeconds(
        Math.Clamp(_settings.IntervalSeconds, SettingsService.MinIntervalSeconds, SettingsService.MaxIntervalSeconds));

    public TimeSpan Remaining
    {
        get
        {
            if (_paused)
            {
                return _pausedRemaining;
            }

            var remaining = _nextDue - DateTimeOffset.Now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public double Progress
    {
        get
        {
            var total = Interval.TotalSeconds;
            if (total <= 0)
            {
                return 0;
            }

            var ratio = 1 - (Remaining.TotalSeconds / total);
            return Math.Clamp(ratio, 0, 1);
        }
    }

    /// <summary>预览下一组（不改变播放状态，结果会被缓存）。</summary>
    public WallpaperGroup? PeekNextGroup()
    {
        if (_disposed || _library.Files.Count == 0)
        {
            return null;
        }

        if (_nextPreviewValid)
        {
            return _nextPreview;
        }

        var before = SnapshotState();
        try
        {
            _nextPreview = BuildGroup();
            _nextPreviewAfterState = SnapshotState();
        }
        finally
        {
            RestoreState(before);
        }

        _nextPreviewValid = _nextPreview is not null;
        return _nextPreview;
    }

    /// <summary>启动流程：扫描壁纸、恢复上次状态或立即应用一组。</summary>
    public async Task StartAsync(bool restoreLastGroup)
    {
        await RefreshLibraryAsync(force: true).ConfigureAwait(true);
        _cursor = Math.Clamp(_settings.Cursor, 0, Math.Max(0, _library.Files.Count - 1));
        await Task.Run(RefreshMonitors).ConfigureAwait(true);

        if (_settings.StartPaused)
        {
            _paused = true;
            _pausedRemaining = Interval;
        }
        else if (restoreLastGroup && TryRestoreLastGroup())
        {
            ScheduleNext();
        }
        else
        {
            await ApplyNextAsync().ConfigureAwait(true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Tick?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>重新扫描壁纸文件夹。</summary>
    public async Task<LibraryScanResult> RefreshLibraryAsync(bool force)
    {
        var result = await _library.ScanAsync(_settings.Folders, _settings.IncludeSubfolders, force)
            .ConfigureAwait(true);

        if (_library.Files.Count == 0)
        {
            _hint = _settings.Folders.Count == 0
                ? "还没有添加壁纸文件夹"
                : "所选文件夹中没有找到图片";
        }
        else
        {
            _hint = null;
        }

        InvalidateNextPreview();
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>壁纸池变化后清空历史与随机袋，避免引用已失效文件。</summary>
    public void ResetPlaybackState()
    {
        _history.Clear();
        _bag.Clear();
        _bagPos = 0;
        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, _library.Files.Count - 1));
        _displayedPaths = Array.Empty<string>();
        InvalidateNextPreview();
    }

    public async Task ApplyNextAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!await _switchGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            if (_library.Files.Count == 0)
            {
                _hint = _settings.Folders.Count == 0
                    ? "还没有添加壁纸文件夹"
                    : "壁纸池为空，请检查文件夹";
                _lastError = null;
                ScheduleNext();
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            var group = TakeNextGroup();
            if (group is null)
            {
                _hint = "没有找到可用的图片文件";
                ScheduleNext();
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            await CommitGroupAsync(group).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            AppLog.Error("切换壁纸时发生异常", ex);
            ScheduleNext();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task ApplyPreviousAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!await _switchGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            if (_history.Count < 2)
            {
                _hint = "已经是第一组了";
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            var target = _history[^2];
            _history.RemoveAt(_history.Count - 1);
            _cursor = target.CursorAfter;
            _settings.Cursor = _cursor;
            _lastError = null;
            InvalidateNextPreview();

            ScheduleNext();
            GroupApplied?.Invoke(this, target);
            await ApplyGroupAsync(target).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            AppLog.Error("切换到上一组时发生异常", ex);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    /// <summary>用当前组重新应用到新布局（多屏模式/缩放方式改变时调用）。</summary>
    public async Task ReapplyCurrentAsync()
    {
        if (_disposed)
        {
            return;
        }

        var current = CurrentGroup;
        if (current is null)
        {
            return;
        }

        if (!await _switchGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await ApplyGroupAsync(current).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("重新应用当前壁纸失败", ex);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public void Pause()
    {
        if (_paused)
        {
            return;
        }

        _pausedRemaining = Remaining;
        if (_pausedRemaining <= TimeSpan.Zero)
        {
            _pausedRemaining = Interval;
        }

        _paused = true;
        _timer.Stop();
        Changed?.Invoke(this, EventArgs.Empty);
        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (!_paused)
        {
            return;
        }

        _paused = false;
        var remaining = _pausedRemaining > TimeSpan.Zero ? _pausedRemaining : Interval;
        _nextDue = DateTimeOffset.Now + remaining;
        _timer.Start();
        Changed?.Invoke(this, EventArgs.Empty);
        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePause()
    {
        if (_paused)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }

    /// <summary>间隔或模式改变后刷新计时。</summary>
    public void OnSettingsChanged()
    {
        if (_paused)
        {
            _pausedRemaining = Interval;
        }
        else
        {
            _nextDue = DateTimeOffset.Now + Interval;
        }

        InvalidateNextPreview();
        Tick?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshMonitors()
    {
        try
        {
            _monitors = _wallpaper.GetMonitors();
        }
        catch (Exception ex)
        {
            AppLog.Error("刷新显示器列表失败", ex);
            _monitors = Array.Empty<MonitorInfo>();
        }
    }

    private async Task CommitGroupAsync(WallpaperGroup group)
    {
        PushHistory(group);
        _settings.Cursor = _cursor;
        _lastError = null;
        _hint = null;
        InvalidateNextPreview();

        ScheduleNext();
        GroupApplied?.Invoke(this, group);
        await ApplyGroupAsync(group).ConfigureAwait(true);
    }

    /// <summary>整组一次性应用到所有显示器。</summary>
    private async Task ApplyGroupAsync(WallpaperGroup group)
    {
        // 显示器枚举走 COM，放到后台线程，避免阻塞 UI
        await Task.Run(RefreshMonitors).ConfigureAwait(true);
        var target = BuildTargetPaths(group, _monitors.Count);
        _displayedPaths = target;
        Changed?.Invoke(this, EventArgs.Empty);

        var (ok, error) = await Task.Run(() =>
        {
            var success = _wallpaper.Apply(group.Paths, _settings.MonitorMode, _settings.Scale, out var err);
            return (success, err);
        }).ConfigureAwait(true);

        if (!ok)
        {
            _lastError = error ?? "设置壁纸失败";
            AppLog.Warn(_lastError);
        }
        else
        {
            _lastError = null;
            _lastPickedPath = target.Length > 0 ? target[^1] : _lastPickedPath;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string[] BuildTargetPaths(WallpaperGroup group, int monitorCount)
    {
        var count = Math.Max(1, monitorCount);
        var target = new string[count];
        var same = _settings.MonitorMode == MultiMonitorMode.SameOnAll || group.Paths.Length == 1;

        for (var i = 0; i < count; i++)
        {
            target[i] = same ? group.Paths[0] : group.Paths[i % group.Paths.Length];
        }

        return target;
    }

    private void ScheduleNext()
    {
        if (_paused)
        {
            return;
        }

        _nextDue = DateTimeOffset.Now + Interval;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }

        Tick?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Tick?.Invoke(this, EventArgs.Empty);

        if (!_paused && DateTimeOffset.Now >= _nextDue)
        {
            _ = ApplyNextAsync();
        }
    }

    private void PushHistory(WallpaperGroup group)
    {
        _history.Add(group);
        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
        }
    }

    private bool TryRestoreLastGroup()
    {
        var paths = _settings.LastGroupPaths;
        if (paths.Count == 0)
        {
            return false;
        }

        if (paths.Any(p => !File.Exists(p)))
        {
            return false;
        }

        if (_settings.MonitorMode == MultiMonitorMode.PerMonitor)
        {
            var expected = Math.Max(1, _monitors.Count);
            if (paths.Count != expected)
            {
                return false;
            }
        }

        var group = new WallpaperGroup(paths.ToArray(), _cursor, _cursor, DateTimeOffset.Now);
        _history.Add(group);
        _displayedPaths = BuildTargetPaths(group, _monitors.Count);
        return true;
    }

    private WallpaperGroup? TakeNextGroup()
    {
        if (_nextPreviewValid
            && _nextPreview is not null
            && _nextPreview.Paths.All(File.Exists))
        {
            RestoreState(_nextPreviewAfterState);
            var group = _nextPreview;
            InvalidateNextPreview();
            return group;
        }

        InvalidateNextPreview();
        return BuildGroup();
    }

    private void InvalidateNextPreview()
    {
        _nextPreview = null;
        _nextPreviewValid = false;
        _nextPreviewAfterState = default;
    }

    private WallpaperGroup? BuildGroup()
    {
        var count = _settings.MonitorMode == MultiMonitorMode.SameOnAll
            ? 1
            : Math.Max(1, _monitors.Count);

        var pool = _library.Files;
        if (pool.Count == 0)
        {
            return null;
        }

        var startCursor = _cursor;
        var picked = new List<string>(count);
        var attempts = 0;
        var maxAttempts = Math.Min(pool.Count * 3, count * 30 + 60);

        while (picked.Count < count && attempts < maxAttempts)
        {
            attempts++;
            var candidate = TakeOne(pool);
            if (candidate is null)
            {
                break;
            }

            if (!File.Exists(candidate))
            {
                continue;
            }

            if (pool.Count >= count
                && picked.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (picked.Count > 0
                && pool.Count > 1
                && string.Equals(picked[^1], candidate, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            picked.Add(candidate);
        }

        if (picked.Count == 0)
        {
            return null;
        }

        if (picked.Count < count)
        {
            var baseList = picked.ToArray();
            for (var i = 0; picked.Count < count; i++)
            {
                picked.Add(baseList[i % baseList.Length]);
            }
        }

        return new WallpaperGroup(picked.ToArray(), startCursor, _cursor, DateTimeOffset.Now);
    }

    private string? TakeOne(IReadOnlyList<string> pool)
    {
        if (pool.Count == 0)
        {
            return null;
        }

        if (_settings.Mode == PlaybackMode.Sequential)
        {
            var path = pool[_cursor % pool.Count];
            _cursor = (_cursor + 1) % pool.Count;
            return path;
        }

        if (_bag.Count == 0 || _bagPos >= _bag.Count)
        {
            RefillBag(pool);
        }

        if (_bag.Count == 0)
        {
            return null;
        }

        var value = _bag[_bagPos % _bag.Count];
        _bagPos++;
        return value;
    }

    private void RefillBag(IReadOnlyList<string> pool)
    {
        _bag.Clear();
        _bag.AddRange(pool);

        for (var i = _bag.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }

        if (_bag.Count > 1
            && _lastPickedPath is not null
            && string.Equals(_bag[0], _lastPickedPath, StringComparison.OrdinalIgnoreCase))
        {
            (_bag[0], _bag[1]) = (_bag[1], _bag[0]);
        }

        _bagPos = 0;
    }

    private (int Cursor, int BagPos, string[] Bag) SnapshotState()
        => (_cursor, _bagPos, _bag.ToArray());

    private void RestoreState((int Cursor, int BagPos, string[] Bag) state)
    {
        _cursor = state.Cursor;
        _bagPos = state.BagPos;
        _bag.Clear();
        _bag.AddRange(state.Bag);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _switchGate.Dispose();
    }
}
