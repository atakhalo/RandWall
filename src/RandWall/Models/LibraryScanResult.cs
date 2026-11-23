using System;
using System.Collections.Generic;

namespace RandWall.Models;

/// <summary>扫描壁纸文件夹的结果。</summary>
public sealed class LibraryScanResult
{
    public LibraryScanResult(IReadOnlyList<string> files, IReadOnlyDictionary<string, int> folderCounts, IReadOnlyList<string> errors)
    {
        Files = files;
        FolderCounts = folderCounts;
        Errors = errors;
    }

    public IReadOnlyList<string> Files { get; }

    /// <summary>每个壁纸文件夹（规范化后的路径）包含的图片数量。</summary>
    public IReadOnlyDictionary<string, int> FolderCounts { get; }

    public IReadOnlyList<string> Errors { get; }

    public static LibraryScanResult Empty { get; } =
        new(Array.Empty<string>(), new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());
}
