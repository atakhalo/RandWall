using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace RandWall.Services;

/// <summary>缩略图解码与有限缓存（LRU，最多 72 张）。缓存按「解码宽度 + 路径」区分，避免大小图串用。</summary>
public sealed class ThumbnailService
{
    private const int MaxCached = 72;

    private readonly object _sync = new();
    private readonly Dictionary<string, Bitmap> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();

    public async Task<Bitmap?> LoadAsync(string path, int decodeWidth = 480)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var key = MakeKey(path, decodeWidth);

        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                Touch(key);
                return cached;
            }
        }

        var bitmap = await Task.Run(() => Decode(path, decodeWidth)).ConfigureAwait(false);
        if (bitmap is null)
        {
            return null;
        }

        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            _cache[key] = bitmap;
            _lru.AddFirst(key);
            while (_lru.Count > MaxCached)
            {
                var last = _lru.Last;
                if (last is null)
                {
                    break;
                }

                _lru.RemoveLast();
                if (_cache.Remove(last.Value, out var evicted) && !ReferenceEquals(evicted, bitmap))
                {
                    evicted.Dispose();
                }
            }

            return bitmap;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            foreach (var bitmap in _cache.Values)
            {
                bitmap.Dispose();
            }

            _cache.Clear();
            _lru.Clear();
        }
    }

    private static string MakeKey(string path, int decodeWidth) => $"{decodeWidth}|{path}";

    private static Bitmap? Decode(string path, int decodeWidth)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"缩略图加载失败 {path}：{ex.Message}");
            return null;
        }
    }

    private void Touch(string key)
    {
        var node = _lru.Find(key);
        if (node is null)
        {
            return;
        }

        _lru.Remove(node);
        _lru.AddFirst(node);
    }
}
