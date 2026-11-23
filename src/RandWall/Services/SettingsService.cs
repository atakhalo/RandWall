using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RandWall.Models;

namespace RandWall.Services;

/// <summary>配置读写：%AppData%\RandWall\settings.json（原子写入，损坏时自动备份重建）。</summary>
public sealed class SettingsService
{
    /// <summary>切换间隔的上下限：1 分钟 ~ 30 天。</summary>
    public const long MinIntervalSeconds = 60;

    public const long MaxIntervalSeconds = 60L * 60 * 24 * 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SettingsService()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RandWall");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
    }

    public string DataDirectory { get; }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Folders ??= new List<string>();
            settings.LastGroupPaths ??= new List<string>();

            // 兼容旧配置（只保存了 intervalMinutes）
            if (settings.IntervalSeconds <= 0)
            {
                settings.IntervalSeconds = settings.IntervalMinutes is > 0
                    ? settings.IntervalMinutes.Value * 60L
                    : 30 * 60;
            }

            settings.IntervalMinutes = null;
            settings.IntervalSeconds = Math.Clamp(settings.IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);
            return settings;
        }
        catch (Exception ex)
        {
            AppLog.Error("读取配置失败，已使用默认配置", ex);
            TryBackupBrokenFile();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            settings.IntervalMinutes = null;
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);

            if (File.Exists(SettingsPath))
            {
                File.Replace(tempPath, SettingsPath, null);
            }
            else
            {
                File.Move(tempPath, SettingsPath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("保存配置失败", ex);
        }
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var backup = Path.Combine(DataDirectory, $"settings.broken.{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Move(SettingsPath, backup, overwrite: true);
        }
        catch
        {
            // 忽略
        }
    }
}
