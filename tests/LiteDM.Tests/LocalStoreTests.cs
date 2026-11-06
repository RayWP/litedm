using System.Text.Json;
using LiteDM.App;
using LiteDM.Core;

namespace LiteDM.Tests;

public sealed class LocalStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedm-store-tests", Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_directory, "state.db");
    private string LegacyPath => Path.Combine(_directory, "state.json");

    [Fact]
    public async Task MissingStateUsesLightAppearanceAndDefaultLimits()
    {
        var result = await new LocalStore(StatePath).LoadAsync();
        Assert.Null(result.Warning);
        Assert.Empty(result.Snapshots);
        Assert.Equal("light", result.Settings.Theme);
        Assert.Equal(1, result.Settings.MaxConcurrentDownloads);
        Assert.Equal(4, result.Settings.SegmentsPerDownload);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"version\":99}")]
    [InlineData("{\"version\":1,\"settings\":{\"maxConcurrentDownloads\":0}}")]
    public async Task CorruptOrUnsupportedLegacyStateIsPreservedAndReportsWarning(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(LegacyPath, content);
        var result = await new LocalStore(StatePath).LoadAsync();
        Assert.NotNull(result.Warning);
        Assert.Empty(result.Snapshots);
        Assert.Equal(content, await File.ReadAllTextAsync(LegacyPath));
    }

    [Fact]
    public async Task SaveRoundTripsSettingsAndPreservesAllUnfinishedJobs()
    {
        var jobs = Enumerable.Range(0, 250).Select(i => Job(i, DownloadState.Paused)).ToArray();
        jobs[0] = jobs[0] with
        {
            Parts = [new(10, 20), new(5, 20)]
        };
        var settings = new AppSettings { Theme = "dark", MaxConcurrentDownloads = 3, SegmentsPerDownload = 8 };
        await new LocalStore(StatePath).SaveAsync(jobs, settings);
        var result = await new LocalStore(StatePath).LoadAsync();
        Assert.Null(result.Warning);
        Assert.Equal(settings, result.Settings);
        Assert.Equal(jobs.Select(j => j.Id), result.Snapshots.Select(j => j.Id));
        Assert.Equal(jobs[0].Parts.AsEnumerable(), result.Snapshots[0].Parts.AsEnumerable());
        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public async Task SaveCapsCompletedHistoryWithoutDroppingUnfinishedJobs()
    {
        var jobs = Enumerable.Range(0, 250).Select(i => Job(i, DownloadState.Completed))
            .Append(Job(250, DownloadState.Downloading)).ToArray();
        await new LocalStore(StatePath).SaveAsync(jobs, new AppSettings());
        var result = await new LocalStore(StatePath).LoadAsync();
        Assert.Equal(20, result.Snapshots.Count(j => j.State == DownloadState.Completed));
        Assert.Contains(result.Snapshots, j => j.Id == jobs[^1].Id);
        Assert.DoesNotContain(result.Snapshots, j => j.Id == jobs[0].Id);
    }

    [Fact]
    public async Task FailedJobsAreRetainedForRetryEvenBeyondHistoryLimit()
    {
        var jobs = Enumerable.Range(0, 250).Select(i => Job(i, DownloadState.Failed)).ToArray();
        await new LocalStore(StatePath).SaveAsync(jobs, new AppSettings());
        Assert.Equal(250, (await new LocalStore(StatePath).LoadAsync()).Snapshots.Count);
    }

    [Fact]
    public async Task LegacyStateImportsOnceAndKeepsResumableJobs()
    {
        Directory.CreateDirectory(_directory);
        var paused = Job(1, DownloadState.Paused);
        var settings = new AppSettings { Theme = "dark" };
        var legacy = new
        {
            Version = 1,
            Settings = settings,
            History = new[] { new { paused.Id, paused.Url, paused.FileName, paused.DestinationDirectory, paused.State } }
        };
        await File.WriteAllTextAsync(LegacyPath, JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var imported = await new LocalStore(StatePath).LoadAsync();
        Assert.Null(imported.Warning);
        Assert.Equal(settings, imported.Settings);
        Assert.Empty(Assert.Single(imported.Snapshots).Parts);
        await File.WriteAllTextAsync(LegacyPath, "{}");
        Assert.Equal(paused.Id, Assert.Single((await new LocalStore(StatePath).LoadAsync()).Snapshots).Id);
    }

    [Fact]
    public void RecentHistoryUsesStatusAndCaseInsensitiveFileNameWithinTheLatestTwenty()
    {
        var recent = DateTimeOffset.UtcNow;
        var old = Job(1, DownloadState.Completed) with
        {
            FileName = "old-only.zip",
            HistoryAtUtc = recent.AddDays(-2)
        };
        var paused = Job(2, DownloadState.Paused) with
        {
            FileName = "MyFile.zip",
            HistoryAtUtc = recent
        };
        var corrupt = Job(3, DownloadState.Failed) with
        {
            IsCorrupt = true,
            HistoryAtUtc = recent.AddDays(-1)
        };
        var jobs = Enumerable.Range(4, 18).Select(i => Job(i, DownloadState.Completed) with { HistoryAtUtc = recent })
            .Append(old).Append(paused).Append(corrupt).ToArray();

        Assert.Equal(20, LocalStore.RecentHistory(jobs).Count());
        Assert.Empty(LocalStore.RecentHistory(jobs, "old-only"));
        Assert.Equal(paused.Id, Assert.Single(LocalStore.RecentHistory(jobs, "myfile", DownloadHistoryStatus.Paused)).Id);
        Assert.Equal(corrupt.Id, Assert.Single(LocalStore.RecentHistory(jobs, status: DownloadHistoryStatus.Corrupt)).Id);
        Assert.Empty(LocalStore.RecentHistory(jobs, status: DownloadHistoryStatus.Failed));
    }

    private DownloadSnapshot Job(int i, DownloadState state) => new()
    {
        Url = $"https://example.com/{i}.bin",
        FileName = $"{i}.bin",
        DestinationDirectory = _directory,
        State = state
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
