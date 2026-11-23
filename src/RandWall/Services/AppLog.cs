using System;
using System.IO;
using System.Text;

namespace RandWall.Services;

/// <summary>极简文件日志（%AppData%\RandWall\logs\app.log），超过 1MB 时滚动一次。</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _logPath;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RandWall", "logs");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        var text = ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}";
        Write("ERROR", text);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                _logPath ??= Path.Combine(LogDirectory, "app.log");

                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 1024 * 1024)
                {
                    var old = Path.Combine(LogDirectory, "app.old.log");
                    File.Move(_logPath, old, overwrite: true);
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写入失败不应影响主流程
        }
    }
}
