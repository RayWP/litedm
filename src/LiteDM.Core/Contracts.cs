using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteDM.Core;

public enum DownloadState { Queued, Downloading, Paused, Completed, Cancelled, Failed }
public enum DownloadHistoryStatus { Failed, Finish, Paused, Corrupt }

public sealed record DownloadOptions
{
    public int MaxConcurrentDownloads { get; init; } = 1;
    public int SegmentsPerDownload { get; init; } = 4;
    public long MinimumSegmentedSize { get; init; } = 8 * 1024 * 1024;
}

public readonly record struct DownloadPartProgress(long DownloadedBytes, long TotalBytes);

public sealed record DownloadSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Url { get; init; }
    public required string FileName { get; init; }
    public required string DestinationDirectory { get; init; }
    public DownloadState State { get; init; } = DownloadState.Queued;
    public DateTimeOffset? HistoryAtUtc { get; init; }
    public long DownloadedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public ImmutableArray<DownloadPartProgress> Parts { get; init; } = [];
    public double BytesPerSecond { get; init; }
    public string? Error { get; init; }
    public bool CanResume { get; init; }
    public bool RequiresRestart { get; init; }
    public bool IsCorrupt { get; init; }
    [JsonIgnore] public bool CanRemove => State is DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled;
    [JsonIgnore] public DownloadHistoryStatus? HistoryStatus => State switch
    {
        DownloadState.Completed => DownloadHistoryStatus.Finish,
        DownloadState.Paused => DownloadHistoryStatus.Paused,
        DownloadState.Failed => IsCorrupt ? DownloadHistoryStatus.Corrupt : DownloadHistoryStatus.Failed,
        _ => null
    };
    [JsonIgnore] public string FullPath => Path.Combine(DestinationDirectory, FileName);
}

public sealed record NativeRequest(string Action, string Url);
public sealed record NativeResponse(bool Accepted, string? Error = null);

public static class NativeProtocol
{
    public const string HostName = "com.litedm.bridge";
    public static string PipeName { get; } = "LiteDM-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16];
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool IsSupportedUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
}
