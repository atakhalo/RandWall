using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RandWall.Services;

/// <summary>一次更新检查的结果。</summary>
public sealed class UpdateCheckResult
{
    public bool Success { get; init; }

    /// <summary>仓库还没有发布过正式版本（404 / 全为草稿或预发布）。</summary>
    public bool NoRelease { get; init; }

    public Version? LatestVersion { get; init; }

    public string TagName { get; init; } = string.Empty;

    public string ReleaseUrl { get; init; } = string.Empty;

    public string? Notes { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public string? Error { get; init; }

    public static UpdateCheckResult Failed(string? error) => new() { Error = error };

    public static UpdateCheckResult NotFound() => new() { NoRelease = true };
}

/// <summary>
/// 更新检查：读取 GitHub Releases 的最新正式版本，并与程序版本比较。
/// 网络失败、仓库没有 release 时都不抛异常，交给调用方决定是否提示。
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/atakhalo/RandWall";

    public const string ReleasesUrl = RepositoryUrl + "/releases";

    private const string LatestReleaseApi = "https://api.github.com/repos/atakhalo/RandWall/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _latestReleaseApi;

    public UpdateService()
        : this(LatestReleaseApi)
    {
    }

    public UpdateService(string latestReleaseApiUrl)
    {
        _latestReleaseApi = latestReleaseApiUrl;
        var assemblyVersion = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
        CurrentVersion = Normalize(assemblyVersion);
    }

    /// <summary>当前程序的版本（形如 1.0.0）。</summary>
    public Version CurrentVersion { get; }

    /// <summary>查询最新正式版本。</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http
                .GetAsync(_latestReleaseApi, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed($"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            {
                return UpdateCheckResult.NotFound();
            }

            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;
            var version = ParseVersion(tag);
            if (version is null)
            {
                return UpdateCheckResult.Failed("无法识别的版本号");
            }

            var url = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            var notes = root.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString()
                : null;

            DateTimeOffset? publishedAt = null;
            if (root.TryGetProperty("published_at", out var publishedElement)
                && publishedElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(publishedElement.GetString(), out var parsed))
            {
                publishedAt = parsed;
            }

            return new UpdateCheckResult
            {
                Success = true,
                LatestVersion = version,
                TagName = tag ?? string.Empty,
                ReleaseUrl = string.IsNullOrWhiteSpace(url) ? ReleasesUrl : url!,
                Notes = notes,
                PublishedAt = publishedAt,
            };
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Failed("检查超时");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"检查更新失败：{ex.Message}");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>解析 release tag：兼容 "1.0.0"、"v1.0.0" 以及带 -preview 后缀的写法。</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            text = text[..dash];
        }

        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>统一成三段版本号，便于比较与显示（1.0 → 1.0.0）。</summary>
    public static Version Normalize(Version version)
        => new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RandWall-UpdateChecker/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool IsTrue(JsonElement root, string property)
        => root.TryGetProperty(property, out var element)
           && element.ValueKind == JsonValueKind.True;
}
