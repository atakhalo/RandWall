using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RandWall.Models;

namespace RandWall.Services;

/// <summary>壁纸池：递归扫描指定文件夹中的图片文件。</summary>
public sealed class WallpaperLibrary
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff",
    };

    private readonly object _sync = new();
    private List<string> _files = new();
    private int _scanVersion;
    private string _scanSignature = string.Empty;

    /// <summary>当前图片池（已排序、去重）。</summary>
    public IReadOnlyList<string> Files
    {
        get
        {
            lock (_sync)
            {
                return _files;
            }
        }
    }

    public LibraryScanResult LastScan { get; private set; } = LibraryScanResult.Empty;

    public static bool IsSupportedImage(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 扫描文件夹。若文件夹与递归设置未变化且已有结果，则直接复用缓存。
    /// </summary>
    public async Task<LibraryScanResult> ScanAsync(
        IReadOnlyList<string> folders,
        bool includeSubfolders,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var signature = BuildSignature(folders, includeSubfolders);
        lock (_sync)
        {
            if (!force && _scanVersion > 0 && signature == _scanSignature && _files.Count > 0)
            {
                return LastScan;
            }
        }

        var version = Interlocked.Increment(ref _scanVersion);
        var result = await Task.Run(() => ScanCore(folders, includeSubfolders, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        lock (_sync)
        {
            if (version == _scanVersion)
            {
                _files = result.Files.ToList();
                _scanSignature = signature;
                LastScan = result;
            }
        }

        return result;
    }

    private static LibraryScanResult ScanCore(IReadOnlyList<string> folders, bool includeSubfolders, CancellationToken token)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = includeSubfolders,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (var folder in folders)
        {
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var normalized = NormalizeFolder(folder);
            if (!Directory.Exists(normalized))
            {
                errors.Add($"{normalized} 不存在或无法访问");
                counts[normalized] = 0;
                continue;
            }

            var count = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(normalized, "*", options))
                {
                    token.ThrowIfCancellationRequested();

                    var name = Path.GetFileName(file);
                    if (name.StartsWith('.'))
                    {
                        continue;
                    }

                    if (!IsSupportedImage(file))
                    {
                        continue;
                    }

                    count++;
                    if (seen.Add(file))
                    {
                        files.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{normalized}：{ex.Message}");
            }

            counts[normalized] = count;
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return new LibraryScanResult(files, counts, errors);
    }

    public static string NormalizeFolder(string folder)
    {
        var trimmed = folder.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
        {
            return folder;
        }

        return Path.GetFullPath(trimmed);
    }

    private static string BuildSignature(IReadOnlyList<string> folders, bool includeSubfolders)
        => string.Join('|', folders.Select(f => $"{NormalizeFolder(f)}#{includeSubfolders}"));

    /// <summary>判断图片是否仍然存在。</summary>
    public static bool StillExists(string path) => File.Exists(path);
}
