using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using RandWall.Models;

namespace RandWall.Services;

/// <summary>
/// 通过 COM 接口 IDesktopWallpaper 设置壁纸，支持每一块显示器独立设置。
/// 系统不支持时回退到 SystemParametersInfo（仅主屏）。
/// </summary>
public sealed class DesktopWallpaperService : IDisposable
{
    private static readonly Guid ClsidDesktopWallpaper = new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");
    private static readonly Guid IidDesktopWallpaper = new("B92B56A9-8B55-4E14-9A89-0199BBB6F93B");

    private const uint ClsCtxAll = 0x17;

    // IDesktopWallpaper 的 vtable 索引（IUnknown 占 0..2）
    private const int ReleaseIndex = 2;
    private const int SetWallpaperIndex = 3;
    private const int GetMonitorDevicePathAtIndex = 5;
    private const int GetMonitorDevicePathCountIndex = 6;
    private const int GetMonitorRectIndex = 7;
    private const int SetPositionIndex = 10;

    private const uint SpiSetDesktopWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    private readonly StaExecutor _sta = new();
    private readonly object _sync = new();

    private IntPtr _com;
    private bool _comCreateFailed;
    private bool _disposed;

    private SetWallpaperFn? _setWallpaper;
    private GetMonitorDevicePathAtFn? _getMonitorAt;
    private GetMonitorDevicePathCountFn? _getMonitorCount;
    private GetMonitorRectFn? _getMonitorRect;
    private SetPositionFn? _setPosition;
    private ReleaseFn? _release;

    /// <summary>是否可逐屏设置壁纸（依赖 IDesktopWallpaper 是否创建成功）。</summary>
    public bool SupportsPerMonitor => GetMonitors().Count > 0 && !_comCreateFailed;

    /// <summary>枚举当前显示器。</summary>
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        try
        {
            return _sta.Invoke(GetMonitorsCore);
        }
        catch (Exception ex)
        {
            AppLog.Error("枚举显示器失败", ex);
            return Array.Empty<MonitorInfo>();
        }
    }

    /// <summary>
    /// 应用一组壁纸。<paramref name="paths"/> 按显示器顺序给出；同一张时只需一个元素。
    /// </summary>
    public bool Apply(IReadOnlyList<string> paths, MultiMonitorMode mode, WallpaperScale scale, out string? error)
    {
        error = null;
        if (paths.Count == 0)
        {
            error = "没有可用的壁纸";
            return false;
        }

        if (!File.Exists(paths[0]))
        {
            error = "壁纸文件不存在";
            return false;
        }

        var (ok, comError) = _sta.Invoke(() => ApplyCore(paths, mode, scale));
        if (ok)
        {
            return true;
        }

        AppLog.Warn($"IDesktopWallpaper 设置失败（{comError}），尝试回退方案");
        if (ApplyViaSystemParameters(paths[0], scale, out var fallbackError))
        {
            return true;
        }

        error = comError is null ? fallbackError : $"{comError}；回退失败：{fallbackError}";
        return false;
    }

    /// <summary>为单块显示器设置壁纸（用于分帧切换）。</summary>
    public bool ApplySingle(MonitorInfo monitor, string path, WallpaperScale scale, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            error = "壁纸文件不存在";
            return false;
        }

        var (ok, comError) = _sta.Invoke(() => ApplySingleCore(monitor.DeviceId, path, scale));
        if (ok)
        {
            return true;
        }

        if (ApplyViaSystemParameters(path, scale, out var fallbackError))
        {
            return true;
        }

        error = comError is null ? fallbackError : $"{comError}；回退失败：{fallbackError}";
        return false;
    }

    private (bool Ok, string? Error) ApplySingleCore(string monitorId, string path, WallpaperScale scale)
    {
        EnsureComUnsafe();
        if (_com == IntPtr.Zero || _setWallpaper is null)
        {
            return (false, "系统不支持 IDesktopWallpaper 接口");
        }

        try
        {
            var hr = _setPosition!(_com, ToNativePosition(scale));
            if (hr != 0)
            {
                AppLog.Warn($"设置壁纸缩放方式失败，HRESULT=0x{hr:X8}");
            }

            hr = CallSetWallpaper(_com, monitorId, path);
            if (hr != 0)
            {
                return (false, $"设置显示器壁纸失败（0x{hr:X8}）");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private (bool Ok, string? Error) ApplyCore(IReadOnlyList<string> paths, MultiMonitorMode mode, WallpaperScale scale)
    {
        EnsureComUnsafe();
        if (_com == IntPtr.Zero || _setWallpaper is null)
        {
            return (false, "系统不支持 IDesktopWallpaper 接口");
        }

        try
        {
            var nativePosition = ToNativePosition(scale);
            var hr = _setPosition!(_com, nativePosition);
            if (hr != 0)
            {
                AppLog.Warn($"设置壁纸缩放方式失败，HRESULT=0x{hr:X8}");
            }

            if (mode == MultiMonitorMode.SameOnAll)
            {
                hr = CallSetWallpaper(_com, null, paths[0]);
                if (hr != 0)
                {
                    return (false, $"设置所有显示器壁纸失败（0x{hr:X8}）");
                }

                return (true, null);
            }

            var monitors = GetMonitorsCore();
            if (monitors.Count == 0)
            {
                return (false, "未检测到显示器");
            }

            for (var i = 0; i < monitors.Count; i++)
            {
                var path = paths[i % paths.Count];
                hr = CallSetWallpaper(_com, monitors[i].DeviceId, path);
                if (hr != 0)
                {
                    return (false, $"设置显示器 {i + 1} 壁纸失败（0x{hr:X8}）");
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private List<MonitorInfo> GetMonitorsCore()
    {
        EnsureComUnsafe();
        var result = new List<MonitorInfo>();
        if (_com == IntPtr.Zero || _getMonitorCount is null || _getMonitorAt is null)
        {
            return result;
        }

        var hr = _getMonitorCount(_com, out var count);
        if (hr != 0 || count == 0 || count > 16)
        {
            return result;
        }

        for (uint i = 0; i < count; i++)
        {
            hr = _getMonitorAt(_com, i, out var idPtr);
            if (hr != 0 || idPtr == IntPtr.Zero)
            {
                continue;
            }

            string deviceId;
            try
            {
                deviceId = Marshal.PtrToStringUni(idPtr) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeCoTaskMem(idPtr);
            }

            if (string.IsNullOrEmpty(deviceId))
            {
                continue;
            }

            var rect = default(NativeRect);
            var monitorPtr = Marshal.StringToCoTaskMemUni(deviceId);
            try
            {
                if (_getMonitorRect is not null)
                {
                    var rectHr = _getMonitorRect(_com, monitorPtr, out rect);
                    if (rectHr != 0)
                    {
                        rect = default;
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(monitorPtr);
            }

            result.Add(new MonitorInfo(
                (int)i,
                deviceId,
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top));
        }

        return result;
    }

    private int CallSetWallpaper(IntPtr com, string? monitorId, string wallpaper)
    {
        var monitorPtr = monitorId is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(monitorId);
        var wallpaperPtr = Marshal.StringToCoTaskMemUni(wallpaper);
        try
        {
            return _setWallpaper!(com, monitorPtr, wallpaperPtr);
        }
        finally
        {
            if (monitorPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(monitorPtr);
            }

            Marshal.FreeCoTaskMem(wallpaperPtr);
        }
    }

    private static bool ApplyViaSystemParameters(string path, WallpaperScale scale, out string? error)
    {
        error = null;
        try
        {
            ApplyLegacyWallpaperStyle(scale);
            var ok = SystemParametersInfo(SpiSetDesktopWallpaper, 0, path, SpifUpdateIniFile | SpifSendChange);
            if (!ok)
            {
                error = $"SystemParametersInfo 失败（Win32 错误码 {Marshal.GetLastWin32Error()}）";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ApplyLegacyWallpaperStyle(WallpaperScale scale)
    {
        // 回退路径需要写注册表让系统按指定方式拉伸
        var (style, tile) = scale switch
        {
            WallpaperScale.Center => ("0", "0"),
            WallpaperScale.Tile => ("0", "1"),
            WallpaperScale.Stretch => ("2", "0"),
            WallpaperScale.Fit => ("6", "0"),
            WallpaperScale.Span => ("22", "0"),
            _ => ("10", "0"),
        };

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Desktop", writable: true);
            key?.SetValue("WallpaperStyle", style);
            key?.SetValue("TileWallpaper", tile);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入壁纸样式注册表失败：{ex.Message}");
        }
    }

    private static int ToNativePosition(WallpaperScale scale) => scale switch
    {
        WallpaperScale.Center => 0,
        WallpaperScale.Tile => 1,
        WallpaperScale.Stretch => 2,
        WallpaperScale.Fit => 3,
        WallpaperScale.Fill => 4,
        WallpaperScale.Span => 5,
        _ => 4,
    };

    private void EnsureComUnsafe()
    {
        if (_com != IntPtr.Zero || _comCreateFailed)
        {
            return;
        }

        try
        {
            var clsid = ClsidDesktopWallpaper;
            var iid = IidDesktopWallpaper;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out var ptr);
            if (hr != 0 || ptr == IntPtr.Zero)
            {
                _comCreateFailed = true;
                AppLog.Warn($"创建 IDesktopWallpaper 失败，HRESULT=0x{hr:X8}");
                return;
            }

            lock (_sync)
            {
                _com = ptr;
                _setWallpaper = GetDelegate<SetWallpaperFn>(_com, SetWallpaperIndex);
                _getMonitorAt = GetDelegate<GetMonitorDevicePathAtFn>(_com, GetMonitorDevicePathAtIndex);
                _getMonitorCount = GetDelegate<GetMonitorDevicePathCountFn>(_com, GetMonitorDevicePathCountIndex);
                _getMonitorRect = GetDelegate<GetMonitorRectFn>(_com, GetMonitorRectIndex);
                _setPosition = GetDelegate<SetPositionFn>(_com, SetPositionIndex);
                _release = GetDelegate<ReleaseFn>(_com, ReleaseIndex);
            }
        }
        catch (Exception ex)
        {
            _comCreateFailed = true;
            AppLog.Error("初始化 IDesktopWallpaper 失败", ex);
        }
    }

    private static T GetDelegate<T>(IntPtr com, int vtableIndex) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(com);
        var fnPtr = Marshal.ReadIntPtr(vtable, vtableIndex * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_com != IntPtr.Zero)
            {
                _sta.Invoke(() =>
                {
                    _release?.Invoke(_com);
                    _com = IntPtr.Zero;
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"释放 IDesktopWallpaper 失败：{ex.Message}");
        }
        finally
        {
            _sta.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetWallpaperFn(IntPtr thisPtr, IntPtr monitorId, IntPtr wallpaper);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetMonitorDevicePathAtFn(IntPtr thisPtr, uint monitorIndex, out IntPtr monitorId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetMonitorDevicePathCountFn(IntPtr thisPtr, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetMonitorRectFn(IntPtr thisPtr, IntPtr monitorId, out NativeRect rect);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetPositionFn(IntPtr thisPtr, int position);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ReleaseFn(IntPtr thisPtr);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);
}
