using System.Security.Cryptography;
using LiteDM.Core;

namespace LiteDM.Tests;

public sealed class DownloadTests
{
    [Fact]
    public async Task RecentlyModifiedResourceWithoutStrongEtagUsesOneStream()
    {
        var bytes = new byte[512 * 1024];
        Random.Shared.NextBytes(bytes);
        await using var server = new HttpFixture(bytes)
        {
            ETag = null,
            LastModified = DateTimeOffset.UtcNow.ToString("R")
        };
        await using var manager = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = 1 });
        var item = await manager.AddAsync(server.BaseUrl + "recent.bin", TempDir());
        var done = await WaitForAsync(manager, item.Id, DownloadState.Completed);
        Assert.Equal(1, server.RangeCount);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(done.FullPath));
    }

    [Fact]
    public async Task SegmentedDownloadMatchesSourceHash()
    {
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i * 37)).ToArray();
        await using var server = new HttpFixture(bytes);
        var dir = TempDir();
        await using var manager = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = 1024, SegmentsPerDownload = 4 });
        var added = await manager.AddAsync(server.BaseUrl + "payload.bin", dir);
        var done = await WaitForAsync(manager, added.Id, DownloadState.Completed);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(done.FullPath))));
        Assert.True(server.RangeCount >= 5); // probe plus segment requests
        Assert.Equal(4, done.Parts.Length);
        Assert.All(done.Parts, part => Assert.Equal(part.TotalBytes, part.DownloadedBytes));
        Assert.Equal(done.DownloadedBytes, done.Parts.Sum(part => part.DownloadedBytes));
        Assert.Empty(Directory.GetDirectories(dir, ".litedm-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task IgnoredRangeFallsBackToSingleStreamAndAddsCollisionSuffix()
    {
        var bytes = Enumerable.Range(0, 32 * 1024).Select(i => (byte)i).ToArray();
        await using var server = new HttpFixture(bytes, supportsRanges: false);
        var dir = TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "payload.bin"), "existing");
        await using var manager = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = 1 });
        var item = await manager.AddAsync(server.BaseUrl + "payload.bin", dir);
        var done = await WaitForAsync(manager, item.Id, DownloadState.Completed);
        Assert.Equal("payload (1).bin", done.FileName);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(done.FullPath))));
        Assert.Equal(0, server.RangeCount);
        Assert.Empty(done.Parts);
    }

    [Fact]
    public async Task UnknownLengthChunkedResponseCompletes()
    {
        var bytes = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(255 - i)).ToArray();
        await using var server = new HttpFixture(bytes, supportsRanges: false, chunked: true);
        var dir = TempDir();
        await using var manager = new DownloadManager();
        var item = await manager.AddAsync(server.BaseUrl + "chunked", dir);
        var done = await WaitForAsync(manager, item.Id, DownloadState.Completed);
        Assert.Null(done.TotalBytes);
        Assert.Equal(bytes.LongLength, done.DownloadedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(done.FullPath))));
    }

    [Fact]
    public async Task ZeroByteResourceCreatesAnEmptyFile()
    {
        await using var server = new HttpFixture([]);
        var dir = TempDir();
        await using var manager = new DownloadManager();
        var item = await manager.AddAsync(server.BaseUrl + "empty", dir);
        var done = await WaitForAsync(manager, item.Id, DownloadState.Completed);
        Assert.Equal(0, new FileInfo(done.FullPath).Length);
        Assert.Equal(0, done.DownloadedBytes);
    }

    [Fact]
    public async Task UnsafeResumeAfterValidatorChangeRequiresRestart()
    {
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i * 13)).ToArray();
        await using var server = new HttpFixture(bytes) { DelayPerChunkMs = 3 };
        var dir = TempDir();
        await using var manager = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = 1024, SegmentsPerDownload = 1 });
        var item = await manager.AddAsync(server.BaseUrl + "changed", dir);
        await WaitUntilAsync(() => server.RangeCount >= 2 && manager.GetJobs().Single().DownloadedBytes > 0);
        server.ETag = "\"v2\"";
        await manager.PauseAsync(item.Id);
        var paused = manager.GetJobs().Single();
        Assert.Equal(DownloadState.Paused, paused.State);
        await manager.ResumeAsync(item.Id);
        var failed = await WaitForAsync(manager, item.Id, DownloadState.Failed);
        Assert.True(failed.RequiresRestart);
        Assert.False(failed.CanResume);
        Assert.Equal(DownloadHistoryStatus.Corrupt, failed.HistoryStatus);
    }

    [Fact]
    public async Task PausedCheckpointRestoresAndResumesWithoutDuplicatingBytes()
    {
        var bytes = Enumerable.Range(0, 3 * 1024 * 1024).Select(i => (byte)(i * 19)).ToArray();
        await using var server = new HttpFixture(bytes) { DelayPerChunkMs = 20 };
        var dir = TempDir();
        var manager = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = 1024, SegmentsPerDownload = 4 });
        var item = await manager.AddAsync(server.BaseUrl + "recover", dir);
        await WaitUntilAsync(() => manager.GetJobs().Single().DownloadedBytes >= 256 * 1024);
        await manager.PauseAsync(item.Id);
        var paused = manager.GetJobs().Single();
        Assert.Equal(DownloadState.Paused, paused.State);
        Assert.True(paused.CanResume);
        Assert.Equal(4, paused.Parts.Length);
        Assert.InRange(paused.Parts.Sum(part => part.DownloadedBytes), 1, bytes.LongLength - 1);
        await manager.DisposeAsync();

        await using var restoredManager = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = 1024, SegmentsPerDownload = 1 });
        await restoredManager.RestoreAsync([paused]);
        Assert.Equal(paused.Parts.AsEnumerable(), restoredManager.GetJobs().Single().Parts.AsEnumerable());
        await restoredManager.ResumeAsync(item.Id);
        var completed = await WaitForAsync(restoredManager, item.Id, DownloadState.Completed);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(completed.FullPath))));
    }

    [Fact]
    public void FilenameIsSanitized()
    {
        Assert.Equal("_unsafe_name.exe", DownloadManager.SafeFileName("../unsafe:name.exe"));
        Assert.Equal("download", DownloadManager.SafeFileName(".."));
        Assert.Equal("_CON.txt", DownloadManager.SafeFileName("CON.txt"));
    }

    [Fact]
    public async Task QueueStartsInFifoOrderAndHonorsConcurrencyLimit()
    {
        var bytes = Enumerable.Range(0, 256 * 1024).Select(i => (byte)i).ToArray();
        await using var server = new HttpFixture(bytes) { DelayPerChunkMs = 5 };
        var dir = TempDir();
        await using var manager = new DownloadManager(new DownloadOptions { MaxConcurrentDownloads = 1, MinimumSegmentedSize = long.MaxValue, SegmentsPerDownload = 1 });
        var started = new List<Guid>();
        manager.onChangedCallback += s =>
        {
            if (s.State == DownloadState.Downloading && !started.Contains(s.Id))
            {
                started.Add(s.Id);
            }
        };
        var first = await manager.AddAsync(server.BaseUrl + "first", dir, "first.bin");
        var second = await manager.AddAsync(server.BaseUrl + "second", dir, "second.bin");
        await WaitForAsync(manager, first.Id, DownloadState.Completed);
        await WaitForAsync(manager, second.Id, DownloadState.Completed);
        Assert.Equal(new[] { first.Id, second.Id }, started);
        Assert.Equal(1, server.MaximumActiveRequests);
    }

    [Fact]
    public async Task ResumingQueuedDownloadMovesItToTheQueueTailWithoutStaleEntries()
    {
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)i).ToArray();
        await using var server = new HttpFixture(bytes) { DelayPerChunkMs = 15 };
        var dir = TempDir();
        await using var manager = new DownloadManager(new DownloadOptions { MaxConcurrentDownloads = 1, MinimumSegmentedSize = long.MaxValue, SegmentsPerDownload = 1 });
        var starts = new List<string>();
        manager.onChangedCallback += s =>
        {
            if (s.State == DownloadState.Downloading && !starts.Contains(s.FileName))
            {
                starts.Add(s.FileName);
            }
        };
        var first = await manager.AddAsync(server.BaseUrl + "first", dir, "first.bin");
        await WaitUntilAsync(() => server.ActiveRequests > 0);
        var second = await manager.AddAsync(server.BaseUrl + "second", dir, "second.bin");
        var third = await manager.AddAsync(server.BaseUrl + "third", dir, "third.bin");
        await manager.PauseAsync(second.Id);
        await manager.ResumeAsync(second.Id);
        await WaitForAsync(manager, first.Id, DownloadState.Completed);
        await WaitForAsync(manager, third.Id, DownloadState.Completed);
        await WaitForAsync(manager, second.Id, DownloadState.Completed);
        Assert.Equal(["first.bin", "third.bin", "second.bin"], starts);
    }

    [Fact]
    public async Task DroppedResponseRetriesAndRedirectIsFollowed()
    {
        var bytes = Enumerable.Range(0, 300 * 1024).Select(i => (byte)(i * 3)).ToArray();
        await using var target = new HttpFixture(bytes) { DropFirstFullResponseAfterBytes = 32768 };
        await using var redirect = new HttpFixture(bytes) { RedirectLocation = target.BaseUrl + "actual" };
        var dir = TempDir();
        await using var manager = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = long.MaxValue });
        var item = await manager.AddAsync(redirect.BaseUrl + "redirect", dir);
        var completed = await WaitForAsync(manager, item.Id, DownloadState.Completed);
        Assert.Equal(3, target.RequestCount); // probe, interrupted transfer, retry
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(completed.FullPath))));
    }

    [Fact]
    public async Task HttpErrorFailsWithoutRetryingPermanentStatus()
    {
        await using var server = new HttpFixture([1, 2, 3]) { ForcedStatus = System.Net.HttpStatusCode.NotFound };
        var dir = TempDir();
        await using var manager = new DownloadManager();
        var item = await manager.AddAsync(server.BaseUrl + "missing", dir);
        var failed = await WaitForAsync(manager, item.Id, DownloadState.Failed);
        Assert.Contains("404", failed.Error);
        Assert.Equal(DownloadHistoryStatus.Failed, failed.HistoryStatus);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task CancelRemovesOnlyOwnedPartialDirectory()
    {
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)i).ToArray();
        await using var server = new HttpFixture(bytes) { DelayPerChunkMs = 10 };
        var dir = TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "keep.txt"), "keep");
        await using var manager = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = 1024, SegmentsPerDownload = 1 });
        var item = await manager.AddAsync(server.BaseUrl + "cancel", dir);
        await WaitUntilAsync(() => manager.GetJobs().Single().DownloadedBytes > 0);
        await manager.CancelAsync(item.Id);
        Assert.Equal(DownloadState.Cancelled, manager.GetJobs().Single().State);
        Assert.Empty(Directory.GetDirectories(dir, ".litedm-*"));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(dir, "keep.txt")));
    }

    [Fact]
    public async Task RemovingStoppedTransfersDeletesOnlyTheirPartialFiles()
    {
        await using var server = new HttpFixture(new byte[2 * 1024 * 1024]) { DelayPerChunkMs = 20 };
        var dir = TempDir();
        await using var manager = new DownloadManager();
        var finished = new DownloadSnapshot
        {
            Url = server.BaseUrl + "finished",
            FileName = "keep.txt",
            DestinationDirectory = dir,
            State = DownloadState.Completed
        };
        await File.WriteAllTextAsync(finished.FullPath, "keep");
        await manager.RestoreAsync([finished]);
        var item = await manager.AddAsync(server.BaseUrl + "paused", dir);
        await WaitUntilAsync(() => manager.GetJobs().Any(job => job.Id == item.Id && job.DownloadedBytes > 0));
        Assert.False(await manager.RemoveStoppedAsync(item.Id));
        await manager.PauseAsync(item.Id);
        Assert.Single(Directory.GetDirectories(dir, ".litedm-*"));

        Assert.True(await manager.RemoveStoppedAsync(item.Id));
        Assert.False(await manager.RemoveStoppedAsync(item.Id));
        Assert.False(await manager.RemoveStoppedAsync(finished.Id));
        Assert.Equal(finished.Id, Assert.Single(manager.GetJobs()).Id);
        Assert.Empty(Directory.GetDirectories(dir, ".litedm-*"));
        Assert.Equal("keep", await File.ReadAllTextAsync(finished.FullPath));
    }

    [Fact]
    public async Task SingleStreamCanResumeAValidatedRange()
    {
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i * 11)).ToArray();
        await using var server = new HttpFixture(bytes) { DelayPerChunkMs = 4 };
        var dir = TempDir();
        await using var manager = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = long.MaxValue, SegmentsPerDownload = 1 });
        var item = await manager.AddAsync(server.BaseUrl + "single-resume", dir);
        await WaitUntilAsync(() => manager.GetJobs().Single().DownloadedBytes >= 256 * 1024);
        await manager.PauseAsync(item.Id);
        var paused = manager.GetJobs().Single();
        Assert.True(paused.CanResume);
        await manager.ResumeAsync(item.Id);
        var completed = await WaitForAsync(manager, item.Id, DownloadState.Completed);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(completed.FullPath))));
    }

    [Fact]
    public async Task ChangedResourceRestartRequirementSurvivesRestore()
    {
        var bytes = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i * 5)).ToArray();
        await using var server = new HttpFixture(bytes) { DelayPerChunkMs = 3 };
        var dir = TempDir();
        var manager = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = 1024, SegmentsPerDownload = 1 });
        var item = await manager.AddAsync(server.BaseUrl + "stale", dir);
        await WaitUntilAsync(() => server.RangeCount >= 2 && manager.GetJobs().Single().DownloadedBytes > 0);
        server.ETag = "\"changed\"";
        await manager.PauseAsync(item.Id);
        await manager.ResumeAsync(item.Id);
        var failed = await WaitForAsync(manager, item.Id, DownloadState.Failed);
        Assert.True(failed.RequiresRestart);
        await manager.DisposeAsync();
        await using var restored = new DownloadManager(new DownloadOptions { MinimumSegmentedSize = 1024, SegmentsPerDownload = 1 });
        await restored.RestoreAsync([failed]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restored.ResumeAsync(item.Id));
        await restored.RestartAsync(item.Id);
        var completed = await WaitForAsync(restored, item.Id, DownloadState.Completed);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(completed.FullPath))));
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litedm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<DownloadSnapshot> WaitForAsync(DownloadManager manager, Guid id, DownloadState state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            var item = manager.GetJobs().Single(x => x.Id == id);
            if (item.State == state)
            {
                return item;
            }
            if (item.State == DownloadState.Failed && state != DownloadState.Failed)
            {
                throw new InvalidOperationException(item.Error);
            }
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
