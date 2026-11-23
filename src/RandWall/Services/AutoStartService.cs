using System;
using System.IO;
using Microsoft.Win32;

namespace RandWall.Services;

/// <summary>开机自启：写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。</summary>
public sealed class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RandWall";

    /// <summary>当前可执行文件路径（dotnet run 时为 apphost，发布后为 RandWall.exe）。</summary>
    public string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)
                && !path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            // 退化情形：通过 dotnet 宿主运行（也兼容单文件发布）
            var candidate = Path.Combine(AppContext.BaseDirectory, "RandWall.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            return path ?? string.Empty;
        }
    }

    /// <summary>注册表里的命令行（带 --minimized）。</summary>
    public string CommandLine => $"\"{ExecutablePath}\" --minimized";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                var value = key?.GetValue(ValueName) as string;
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                return value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase)
                       || value.Contains("RandWall", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"读取开机自启状态失败：{ex.Message}");
                return false;
            }
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                if (string.IsNullOrEmpty(ExecutablePath) || !File.Exists(ExecutablePath))
                {
                    AppLog.Warn("无法设置开机自启：可执行文件路径无效");
                    return false;
                }

                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("设置开机自启失败", ex);
            return false;
        }
    }
}
