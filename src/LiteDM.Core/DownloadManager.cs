using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Diagnostics;

namespace LiteDM.Core;

public sealed class DownloadManager : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly Queue<Guid> _queue = [];
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private DownloadOptions _options;
    private int _active;
    private bool _shutdown;

    public DownloadManager(DownloadOptions? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? new DownloadOptions();
        ValidateOptions(_options);
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = httpClient is null;
    }

    public event Action<DownloadSnapshot>? onChangedCallback;

    public IReadOnlyList<DownloadSnapshot> GetJobs()
    {
        lock (_sync)
        {
            return _jobs.Values.Select(j => j.Snapshot).ToArray();
        }
    }

    public async Task<DownloadSnapshot> AddAsync(string url, string destinationDirectory, string? fileName = null)
    {
        if (!NativeProtocol.IsSupportedUrl(url))
        {
            throw new ArgumentException("Only HTTP and HTTPS URLs are supported.", nameof(url));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var name = SafeFileName(fileName ?? FilenameFromUrl(url));
        var snapshot = new DownloadSnapshot { Url = url, DestinationDirectory = Path.GetFullPath(destinationDirectory), FileName = name };
        var job = new Job(snapshot);
        lock (_sync)
        {
            ThrowIfShutdown();
            _jobs.Add(snapshot.Id, job);
            _queue.Enqueue(snapshot.Id);
        }
        Publish(snapshot);
        Schedule();
        await Task.CompletedTask;
        return snapshot;
    }

    public async Task RestoreAsync(IEnumerable<DownloadSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        foreach (var input in snapshots)
        {
            if (!NativeProtocol.IsSupportedUrl(input.Url) || input.Id == Guid.Empty)
            {
                continue;
            }
            var interrupted = input.State is DownloadState.Downloading or DownloadState.Queued;
            var restored = input with
            {
                State = interrupted ? DownloadState.Paused : input.State,
                HistoryAtUtc = interrupted ? DateTimeOffset.UtcNow : input.HistoryAtUtc,
                BytesPerSecond = 0
            };
            var job = new Job(restored);
            if (restored.State is DownloadState.Paused or DownloadState.Failed)
            {
                LoadCheckpoint(job);
                if (input.RequiresRestart)
                {
                    if (job.Checkpoint is not null)
                    {
                        job.Checkpoint.CanResume = false;
                    }
                    job.Snapshot = job.Snapshot with
                    {
                        RequiresRestart = true,
                        CanResume = false
                    };
                }
            }
            lock (_sync)
            {
                if (_shutdown)
                {
                    throw new ObjectDisposedException(nameof(DownloadManager));
                }
                if (_jobs.ContainsKey(restored.Id))
                {
                    continue;
                }
                _jobs.Add(restored.Id, job);
            }
            Publish(job.Snapshot);
        }
        await Task.CompletedTask;
    }

    public async Task PauseAsync(Guid id)
    {
        var job = Find(id);
        await job.Lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? running;
            lock (_sync)
            {
                if (job.Snapshot.State == DownloadState.Queued)
                {
                    RemoveQueuedId(id);
                    job.Snapshot = job.Snapshot with
                    {
                        State = DownloadState.Paused,
                        HistoryAtUtc = DateTimeOffset.UtcNow
                    };
                    Publish(job.Snapshot);
                    return;
                }
                if (job.Snapshot.State != DownloadState.Downloading)
                {
                    return;
                }
                job.PauseRequested = true;
                job.Cancellation?.Cancel();
                running = job.Running;
            }
            if (running is not null)
            {
                await running.ConfigureAwait(false);
            }
        }
        finally
        {
            job.Lifecycle.Release();
        }
    }

    public async Task ResumeAsync(Guid id)
    {
        var job = Find(id);
        await job.Lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (job.Snapshot.State is not (DownloadState.Paused or DownloadState.Queued or DownloadState.Failed))
                {
                    return;
                }
                if (job.Snapshot.RequiresRestart)
                {
                    throw new InvalidOperationException("This download requires a restart before it can continue.");
                }
                job.Snapshot = job.Snapshot with
                {
                    State = DownloadState.Queued,
                    Error = null
                };
                RemoveQueuedId(id);
                _queue.Enqueue(id);
            }
            Publish(job.Snapshot);
            Schedule();
        }
        finally
        {
            job.Lifecycle.Release();
        }
    }

    public async Task RestartAsync(Guid id)
    {
        var job = Find(id);
        await job.Lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? running;
            lock (_sync)
            {
                if (!_jobs.ContainsKey(id))
                {
                    return;
                }
                if (job.Snapshot.State == DownloadState.Downloading)
                {
                    job.PauseRequested = true;
                    job.Cancellation?.Cancel();
                }
                running = job.Running;
            }
            if (running is not null)
            {
                await running.ConfigureAwait(false);
            }
            DeleteOwnedTemp(job);
            lock (_sync)
            {
                job.Checkpoint = null;
                job.Snapshot = job.Snapshot with
                {
                    State = DownloadState.Queued,
                    HistoryAtUtc = null,
                    DownloadedBytes = 0,
                    TotalBytes = null,
                    Parts = [],
                    BytesPerSecond = 0,
                    Error = null,
                    CanResume = false,
                    RequiresRestart = false,
                    IsCorrupt = false
                };
                job.PauseRequested = false;
                RemoveQueuedId(id);
                _queue.Enqueue(id);
            }
            Publish(job.Snapshot);
            Schedule();
        }
        finally
        {
            job.Lifecycle.Release();
        }
    }

    public async Task CancelAsync(Guid id)
    {
        var job = Find(id);
        await job.Lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? running;
            lock (_sync)
            {
                if (IsTerminal(job.Snapshot.State))
                {
                    return;
                }
                RemoveQueuedId(id);
                job.PauseRequested = false;
                job.CancelRequested = true;
                job.Cancellation?.Cancel();
                if (job.Snapshot.State != DownloadState.Downloading)
                {
                    job.Snapshot = job.Snapshot with
                    {
                        State = DownloadState.Cancelled
                    };
                }
                running = job.Running;
            }
            if (running is not null)
            {
                await running.ConfigureAwait(false);
            }
            DeleteOwnedTemp(job);
            lock (_sync)
            {
                job.Snapshot = job.Snapshot with
                {
                    State = DownloadState.Cancelled,
                    DownloadedBytes = 0,
                    Parts = [],
                    CanResume = false,
                    RequiresRestart = false
                };
            }
            Publish(job.Snapshot);
            Schedule();
        }
        finally
        {
            job.Lifecycle.Release();
        }
    }

    public async Task<bool> RemoveStoppedAsync(Guid id)
    {
        Job? job;
        lock (_sync)
        {
            _jobs.TryGetValue(id, out job);
        }
        if (job is null)
        {
            return false;
        }

        await job.Lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? running;
            lock (_sync)
            {
                if (!_jobs.ContainsKey(id) || !job.Snapshot.CanRemove)
                {
                    return false;
                }
                running = job.Running;
            }
            if (running is not null)
            {
                await running.ConfigureAwait(false);
            }
            await Task.Run(() => DeleteTempDirectory(job)).ConfigureAwait(false);

            lock (_sync)
            {
                // A command already waiting on this job must see that it has stopped.
                job.Snapshot = job.Snapshot with
                {
                    State = DownloadState.Cancelled
                };
                RemoveQueuedId(id);
                return _jobs.Remove(id);
            }
        }
        finally
        {
            job.Lifecycle.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        Task[] running;
        lock (_sync)
        {
            if (_shutdown)
            {
                return;
            }
            _shutdown = true;
            foreach (var j in _jobs.Values.Where(j => j.Snapshot.State == DownloadState.Downloading))
            {
                j.PauseRequested = true;
                j.Cancellation?.Cancel();
            }
            foreach (var j in _jobs.Values.Where(j => j.Snapshot.State == DownloadState.Queued))
            {
                j.Snapshot = j.Snapshot with
                {
                    State = DownloadState.Paused,
                    HistoryAtUtc = DateTimeOffset.UtcNow
                };
            }
            running = _jobs.Values.Select(j => j.Running).Where(t => t is not null).Cast<Task>().ToArray();
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    public void UpdateOptions(DownloadOptions options)
    {
        ValidateOptions(options);
        lock (_sync)
        {
            _options = options;
        }
        Schedule();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    public static string FilenameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var value = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return SafeFileName(value);
            }
        }
        return "download";
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(name.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray()).Trim().Trim('.');
        if (safe is "" or "." or "..")
        {
            return "download";
        }
        var stem = Path.GetFileNameWithoutExtension(safe);
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
        {
            safe = "_" + safe;
        }
        return safe.Length > 180 ? safe[..180] : safe;
    }

    private void Schedule()
    {
        List<(Job Job, TaskCompletionSource Signal)> start = [];
        lock (_sync)
        {
            if (_shutdown)
            {
                return;
            }
            while (_active < _options.MaxConcurrentDownloads && _queue.Count > 0)
            {
                var id = _queue.Dequeue();
                if (!_jobs.TryGetValue(id, out var job) || job.Snapshot.State != DownloadState.Queued)
                {
                    continue;
                }
                _active++;
                job.Snapshot = job.Snapshot with
                {
                    State = DownloadState.Downloading,
                    Error = null
                };
                job.StartTimestamp = Stopwatch.GetTimestamp();
                job.StartDownloadedBytes = job.Snapshot.DownloadedBytes;
                job.Cancellation = new CancellationTokenSource();
                job.PauseRequested = job.CancelRequested = false;
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var token = job.Cancellation.Token;
                job.Running = Task.Run(async () =>
                {
                    await signal.Task.ConfigureAwait(false);
                    await RunAsync(job, token).ConfigureAwait(false);
                });
                start.Add((job, signal));
            }
        }
        foreach (var item in start)
        {
            Publish(item.Job.Snapshot);
        }
        foreach (var item in start)
        {
            item.Signal.TrySetResult();
        }
    }

    private async Task RunAsync(Job job, CancellationToken token)
    {
        try
        {
            if (job.Checkpoint is null)
            {
                await ProbeAsync(job, token).ConfigureAwait(false);
            }
            else
            {
                RestoreSavedOffsets(job);
            }
            if (job.Checkpoint?.Segmented == true)
            {
                await DownloadSegmentsAsync(job, token).ConfigureAwait(false);
            }
            else
            {
                await DownloadSingleAsync(job, token).ConfigureAwait(false);
            }
            await AssembleAndCommitAsync(job, token).ConfigureAwait(false);
            lock (_sync)
            {
                job.Snapshot = job.Snapshot with
                {
                    State = DownloadState.Completed,
                    HistoryAtUtc = DateTimeOffset.UtcNow,
                    DownloadedBytes = job.Checkpoint!.Segments.Sum(s => s.SavedOffset),
                    BytesPerSecond = 0,
                    Error = null,
                    CanResume = false,
                    RequiresRestart = false
                };
            }
            DeleteOwnedTemp(job);
            Publish(job.Snapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_sync)
            {
                if (job.CancelRequested)
                {
                    job.Snapshot = job.Snapshot with
                    {
                        State = DownloadState.Cancelled,
                        BytesPerSecond = 0,
                        CanResume = false
                    };
                }
                else if (job.PauseRequested || _shutdown)
                {
                    job.Snapshot = job.Snapshot with
                    {
                        State = DownloadState.Paused,
                        HistoryAtUtc = DateTimeOffset.UtcNow,
                        BytesPerSecond = 0,
                        CanResume = job.Checkpoint?.CanResume == true,
                        RequiresRestart = job.Checkpoint is not null && !job.Checkpoint.CanResume && job.Snapshot.DownloadedBytes > 0
                    };
                }
            }
            SaveCheckpoint(job);
            RestoreSavedOffsets(job);
            Publish(job.Snapshot);
        }
        catch (ResourceChangedException ex)
        {
            lock (_sync)
            {
                job.Snapshot = job.Snapshot with
                {
                    State = DownloadState.Failed,
                    HistoryAtUtc = DateTimeOffset.UtcNow,
                    BytesPerSecond = 0,
                    Error = ex.Message,
                    CanResume = false,
                    RequiresRestart = true,
                    IsCorrupt = true
                };
            }
            SaveCheckpoint(job);
            Publish(job.Snapshot);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                job.Snapshot = job.Snapshot with
                {
                    State = DownloadState.Failed,
                    HistoryAtUtc = DateTimeOffset.UtcNow,
                    BytesPerSecond = 0,
                    Error = ex.Message,
                    CanResume = job.Checkpoint?.CanResume == true,
                    RequiresRestart = job.Checkpoint is null || !job.Checkpoint.CanResume
                };
            }
            SaveCheckpoint(job);
            Publish(job.Snapshot);
        }
        finally
        {
            lock (_sync)
            {
                _active--;
                job.Cancellation?.Dispose();
                job.Cancellation = null;
                job.Running = null;
            }
            Schedule();
        }
    }

    private async Task ProbeAsync(Job job, CancellationToken token)
    {
        HttpResponseMessage response;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = NewRequest(job, HttpMethod.Get);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                response = await SendWithTimeoutAsync(request, token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                {
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"Resource probe failed with HTTP {(int)status}.", null, status);
                }
                break;
            }
            catch (HttpRequestException ex) when (attempt < 3 && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
            catch (TransientNetworkException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
        }
        using (response)
        {
            var zeroLength = response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && response.Content.Headers.ContentRange?.Length == 0;
            if (!response.IsSuccessStatusCode && !zeroLength)
            {
                throw new HttpRequestException($"Resource probe failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            }
            var length = zeroLength ? 0 : response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
            var validRange = response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange is { From: 0, To: 0, Length: not null } range
                && range.Length == length;
            if (response.Content.Headers.ContentEncoding.Any(x => !string.Equals(x, "identity", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The server returned an encoded response; safe range downloading is unavailable.");
            }
            var etag = response.Headers.ETag;
            var lm = response.Content.Headers.LastModified;
            var strongEtag = etag is { IsWeak: false };
            // HTTP dates only distinguish resource versions safely when the response date is sufficiently later.
            var strongDate = lm is { } modified && response.Headers.Date is { } responseDate
                && responseDate - modified >= TimeSpan.FromSeconds(60);
            var canRange = validRange && length.HasValue && (strongEtag || strongDate);
            var segment = canRange && length >= _options.MinimumSegmentedSize;
            job.Checkpoint = new Checkpoint
            {
                Url = job.Snapshot.Url,
                FileName = job.Snapshot.FileName,
                DestinationDirectory = job.Snapshot.DestinationDirectory,
                Length = length,
                ETag = strongEtag ? etag?.ToString() : null,
                LastModified = lm?.ToString("R"),
                Segmented = segment,
                CanResume = canRange
            };
            if (segment)
            {
                var n = Math.Clamp(_options.SegmentsPerDownload, 1, 16);
                for (var i = 0; i < n; i++)
                {
                    var from = length!.Value * i / n;
                    var to = length.Value * (i + 1) / n - 1;
                    job.Checkpoint.Segments.Add(new Segment { From = from, To = to, Path = Path.Combine(TempDirectory(job), $"segment-{i:D2}.part") });
                }
            }
            else
            {
                job.Checkpoint.Segments.Add(new Segment { From = 0, To = length.HasValue ? length.Value - 1 : -1, Path = Path.Combine(TempDirectory(job), "stream.part") });
            }
            Directory.CreateDirectory(TempDirectory(job));
            job.Snapshot = job.Snapshot with
            {
                TotalBytes = length,
                DownloadedBytes = 0,
                Parts = GetPartProgress(job.Checkpoint),
                CanResume = canRange,
                RequiresRestart = false
            };
            SaveCheckpoint(job);
            Publish(job.Snapshot);
        }
    }

    private async Task DownloadSegmentsAsync(Job job, CancellationToken token)
    {
        var cp = job.Checkpoint!;
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? failure = null;
        var workers = cp.Segments.Select(async s =>
        {
            try
            {
                await DownloadSegmentAsync(job, s, siblings.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                siblings.Cancel();
            }
        }).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        token.ThrowIfCancellationRequested();
    }

    private async Task DownloadSegmentAsync(Job job, Segment segment, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            segment.CurrentRequestBytes = 0;
            try
            {
                var start = segment.From + segment.Offset;
                if (start > segment.To)
                {
                    return;
                }
                using var request = NewRequest(job, HttpMethod.Get);
                request.Headers.Range = new RangeHeaderValue(start, segment.To);
                SetIfRange(request, job.Checkpoint!);
                using var response = await SendWithTimeoutAsync(request, token).ConfigureAwait(false);
                ThrowIfTransientStatus(response.StatusCode, "Download request");
                ValidateRangeResponse(response, job.Checkpoint!, start, segment.To);
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(segment.Path)!);
                await using var output = new FileStream(segment.Path, segment.Offset == 0 ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                output.SetLength(segment.Offset);
                output.Position = segment.Offset;
                await CopyCheckpointedAsync(job, segment, input, output, token).ConfigureAwait(false);
                if (segment.Offset != segment.To - segment.From + 1)
                {
                    throw new EndOfStreamException("The server ended a range before all requested bytes arrived.");
                }
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ResourceChangedException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < 3 && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
            catch (TransientNetworkException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
            catch (EndOfStreamException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadSingleAsync(Job job, CancellationToken token)
    {
        var cp = job.Checkpoint!;
        var segment = cp.Segments[0];
        if (cp.Length == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(segment.Path)!);
            using (File.Create(segment.Path))
            {
            }
            SaveCheckpoint(job);
            return;
        }
        if (cp.Length.HasValue && segment.Offset >= cp.Length.Value)
        {
            return;
        }
        // A non-range resource is restarted from byte zero after interruptions; never append an unvalidated response.
        if (!cp.CanResume)
        {
            segment.Offset = 0;
        }
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var request = NewRequest(job, HttpMethod.Get);
                if (cp.CanResume && segment.Offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(segment.Offset, null);
                    SetIfRange(request, cp);
                }
                using var response = await SendWithTimeoutAsync(request, token).ConfigureAwait(false);
                ThrowIfTransientStatus(response.StatusCode, "Download request");
                if (cp.CanResume && segment.Offset > 0)
                {
                    ValidateRangeResponse(response, cp, segment.Offset, cp.Length!.Value - 1);
                }
                else if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    if (cp.Length is null)
                    {
                        throw new ResourceChangedException("The server returned an unexpected partial response. Restart this download.");
                    }
                    ValidateRangeResponse(response, cp, 0, cp.Length.Value - 1);
                }
                else if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new HttpRequestException($"Download request failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
                }
                ValidateIdentity(response);
                ValidateValidator(response, cp);
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(segment.Path)!);
                await using var output = new FileStream(segment.Path, segment.Offset > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                output.SetLength(segment.Offset);
                output.Position = segment.Offset;
                await CopyCheckpointedAsync(job, segment, input, output, token).ConfigureAwait(false);
                if (cp.Length.HasValue && segment.Offset != cp.Length.Value)
                {
                    throw new EndOfStreamException("The server ended before the advertised file length.");
                }
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ResourceChangedException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < 3 && IsTransient(ex))
            {
                if (!cp.CanResume)
                {
                    segment.Offset = segment.SavedOffset = 0;
                    segment.CurrentRequestBytes = 0;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
            catch (TransientNetworkException) when (attempt < 3)
            {
                if (!cp.CanResume)
                {
                    segment.Offset = segment.SavedOffset = 0;
                    segment.CurrentRequestBytes = 0;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
            catch (EndOfStreamException) when (attempt < 3)
            {
                if (!cp.CanResume)
                {
                    segment.Offset = segment.SavedOffset = 0;
                    segment.CurrentRequestBytes = 0;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
        }
    }

    private async Task CopyCheckpointedAsync(Job job, Segment segment, Stream input, Stream output, CancellationToken token)
    {
        var buffer = new byte[65536];
        while (true)
        {
            int read;
            try
            {
                read = await ReadWithIdleTimeoutAsync(input, buffer, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException ex)
            {
                throw new TransientNetworkException("The response stream was interrupted.", ex);
            }
            if (read == 0)
            {
                break;
            }
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            segment.Offset += read;
            segment.CurrentRequestBytes += read;
            if (segment.To >= segment.From && segment.Offset > segment.To - segment.From + 1)
            {
                throw new ResourceChangedException("The server sent more bytes than the requested range. Restart this download.");
            }
            if (segment.Offset % (256 * 1024) < read || read < buffer.Length)
            {
                await output.FlushAsync(token).ConfigureAwait(false);
                if (output is FileStream checkpointStream)
                {
                    checkpointStream.Flush(true);
                }
                ReportCheckpoint(job, segment);
            }
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        if (output is FileStream finalStream)
        {
            finalStream.Flush(true);
        }
        ReportCheckpoint(job, segment);
    }

    private void ReportCheckpoint(Job job, Segment segment)
    {
        // Each update includes all flushed offsets and is published in checkpoint order.
        lock (job.CheckpointSync)
        {
            segment.SavedOffset = segment.Offset;
            var checkpoint = job.Checkpoint!;
            var downloaded = checkpoint.Segments.Sum(s => s.SavedOffset);
            var elapsed = Math.Max(0.001, Stopwatch.GetElapsedTime(job.StartTimestamp).TotalSeconds);
            job.Snapshot = job.Snapshot with
            {
                DownloadedBytes = downloaded,
                Parts = GetPartProgress(checkpoint),
                BytesPerSecond = Math.Max(0, downloaded - job.StartDownloadedBytes) / elapsed
            };
            SaveCheckpoint(job);
            Publish(job.Snapshot);
        }
    }

    private static ImmutableArray<DownloadPartProgress> GetPartProgress(Checkpoint checkpoint)
    {
        if (checkpoint.Segments.Count < 2)
        {
            return [];
        }
        return [.. checkpoint.Segments.Select(s => new DownloadPartProgress(s.SavedOffset, s.To - s.From + 1))];
    }

    private HttpRequestMessage NewRequest(Job job, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, job.Snapshot.Url);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        return request;
    }

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TransientNetworkException("The server did not respond within 30 seconds.", ex);
        }
    }

    private static async Task<int> ReadWithIdleTimeoutAsync(Stream input, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TransientNetworkException("The response was idle for 30 seconds.", ex);
        }
    }

    private static void SetIfRange(HttpRequestMessage request, Checkpoint cp)
    {
        if (cp.ETag is not null)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(cp.ETag));
        }
        else if (cp.LastModified is not null && DateTimeOffset.TryParse(cp.LastModified, out var date))
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(date);
        }
    }

    private static void ValidateRangeResponse(HttpResponseMessage response, Checkpoint cp, long from, long to)
    {
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            throw new HttpRequestException($"Download request failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
        ValidateIdentity(response);
        ValidateValidator(response, cp);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != from || range.To != to || (cp.Length.HasValue && range.Length != cp.Length))
        {
            throw new ResourceChangedException("The server did not return the requested byte range. Restart this download to use the current resource.");
        }
    }

    private static void ThrowIfTransientStatus(HttpStatusCode status, string operation)
    {
        if ((int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException($"{operation} received HTTP {(int)status}.", null, status);
        }
    }

    private static bool IsTransient(HttpRequestException ex) => ex.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int?)ex.StatusCode >= 500;

    private static void ValidateIdentity(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(x => !string.Equals(x, "identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ResourceChangedException("The server changed the response encoding. Restart this download.");
        }
    }

    private static void ValidateValidator(HttpResponseMessage response, Checkpoint cp)
    {
        if (cp.ETag is not null)
        {
            if (response.Headers.ETag?.ToString() != cp.ETag)
            {
                throw new ResourceChangedException("The resource ETag changed. Restart this download.");
            }
        }
        else if (cp.LastModified is not null && response.Content.Headers.LastModified?.ToString("R") != cp.LastModified)
        {
            throw new ResourceChangedException("The resource Last-Modified value changed. Restart this download.");
        }
    }

    private async Task AssembleAndCommitAsync(Job job, CancellationToken token)
    {
        var cp = job.Checkpoint!;
        var temp = Path.Combine(TempDirectory(job), "assembled.part");
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            foreach (var segment in cp.Segments)
            {
                token.ThrowIfCancellationRequested();
                await using var input = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(true);
        }
        var destination = job.Snapshot.DestinationDirectory;
        Directory.CreateDirectory(destination);
        var baseName = Path.GetFileNameWithoutExtension(job.Snapshot.FileName);
        var extension = Path.GetExtension(job.Snapshot.FileName);
        var candidate = "";
        for (var collision = 0; ; collision++)
        {
            candidate = Path.Combine(destination, collision == 0 ? job.Snapshot.FileName : $"{baseName} ({collision}){extension}");
            try
            {
                File.Move(temp, candidate);
                break;
            }
            catch (IOException) when (File.Exists(candidate)) { }
        }
        job.Snapshot = job.Snapshot with
        {
            FileName = Path.GetFileName(candidate),
            DestinationDirectory = destination
        };
    }

    private void LoadCheckpoint(Job job)
    {
        var path = Path.Combine(TempDirectory(job), "metadata.json");
        try
        {
            var cp = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path));
            if (cp is null || cp.Url != job.Snapshot.Url || cp.FileName != job.Snapshot.FileName || cp.DestinationDirectory != job.Snapshot.DestinationDirectory)
            {
                throw new InvalidDataException();
            }
            ValidateCheckpoint(cp);
            for (var i = 0; i < cp.Segments.Count; i++)
            {
                var seg = cp.Segments[i];
                var expectedRoot = Path.GetFullPath(TempDirectory(job)) + Path.DirectorySeparatorChar;
                var actualPath = Path.GetFullPath(seg.Path);
                if (!actualPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Checkpoint segment path escaped its job directory.");
                }
                var expectedName = cp.Segmented ? $"segment-{i:D2}.part" : "stream.part";
                if (!string.Equals(actualPath, Path.GetFullPath(Path.Combine(TempDirectory(job), expectedName)), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Checkpoint segment path is invalid.");
                }
                if (!File.Exists(seg.Path))
                {
                    seg.Offset = seg.SavedOffset = 0;
                    continue;
                }
                var actual = new FileInfo(seg.Path).Length;
                seg.Offset = Math.Min(actual, seg.SavedOffset);
                seg.SavedOffset = seg.Offset;
                if (actual > seg.Offset)
                {
                    using (var f = new FileStream(seg.Path, FileMode.Open, FileAccess.Write, FileShare.None))
                        f.SetLength(seg.Offset);
                }
            }
            job.Checkpoint = cp;
            var sum = cp.Segments.Sum(s => s.Offset);
            job.Snapshot = job.Snapshot with
            {
                DownloadedBytes = sum,
                TotalBytes = cp.Length,
                Parts = GetPartProgress(cp),
                CanResume = cp.CanResume,
                RequiresRestart = !cp.CanResume
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            job.Checkpoint = null;
            job.Snapshot = job.Snapshot with
            {
                State = DownloadState.Failed,
                HistoryAtUtc = DateTimeOffset.UtcNow,
                IsCorrupt = true,
                Error = "The saved download parts could not be validated. Restart the download.",
                DownloadedBytes = 0,
                Parts = [],
                CanResume = false,
                RequiresRestart = true
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            job.Checkpoint = null;
            job.Snapshot = job.Snapshot with
            {
                DownloadedBytes = 0,
                Parts = [],
                CanResume = false,
                RequiresRestart = job.Snapshot.DownloadedBytes > 0
            };
        }
    }

    private static void ValidateCheckpoint(Checkpoint cp)
    {
        if (cp.Length < 0 || cp.Segments is null || cp.Segments.Count == 0)
        {
            throw new InvalidDataException("Checkpoint layout is empty or invalid.");
        }
        if (cp.CanResume && cp.ETag is null && cp.LastModified is null)
        {
            throw new InvalidDataException("A resumable checkpoint requires a validator.");
        }
        if (cp.ETag is not null && (!EntityTagHeaderValue.TryParse(cp.ETag, out var tag) || tag.IsWeak))
        {
            throw new InvalidDataException("Checkpoint ETag is not strong.");
        }
        if (cp.LastModified is not null && !DateTimeOffset.TryParse(cp.LastModified, out _))
        {
            throw new InvalidDataException("Checkpoint Last-Modified value is invalid.");
        }
        if (!cp.Length.HasValue && cp.Segmented)
        {
            throw new InvalidDataException("Unknown-length resources cannot have segmented checkpoints.");
        }

        if (!cp.Segmented)
        {
            var only = cp.Segments[0];
            if (cp.Segments.Count != 1 || only.From != 0 || only.To != (cp.Length.HasValue ? cp.Length.Value - 1 : -1))
            {
                throw new InvalidDataException("Single-stream checkpoint range is invalid.");
            }
            if (only.SavedOffset < 0 || (cp.Length.HasValue && only.SavedOffset > cp.Length.Value))
            {
                throw new InvalidDataException("Single-stream checkpoint offset is invalid.");
            }
            return;
        }

        if (cp.Length is null || cp.Length.Value <= 0)
        {
            throw new InvalidDataException("Segmented checkpoint length is invalid.");
        }
        long expectedFrom = 0;
        foreach (var segment in cp.Segments)
        {
            var count = segment.To - segment.From + 1;
            if (segment.From != expectedFrom || count <= 0 || segment.SavedOffset < 0 || segment.SavedOffset > count)
            {
                throw new InvalidDataException("Segment ranges or offsets are invalid.");
            }
            expectedFrom = segment.To + 1;
        }
        if (expectedFrom != cp.Length.Value)
        {
            throw new InvalidDataException("Segment ranges do not cover the resource exactly.");
        }
    }

    private static void RestoreSavedOffsets(Job job)
    {
        if (job.Checkpoint is null)
        {
            return;
        }
        foreach (var segment in job.Checkpoint.Segments)
        {
            segment.Offset = segment.SavedOffset;
            segment.CurrentRequestBytes = 0;
            if (File.Exists(segment.Path))
            {
                using var file = new FileStream(segment.Path, FileMode.Open, FileAccess.Write, FileShare.None);
                if (file.Length != segment.Offset)
                {
                    file.SetLength(segment.Offset);
                }
            }
            else
            {
                segment.Offset = segment.SavedOffset = 0;
            }
        }
        job.Snapshot = job.Snapshot with
        {
            DownloadedBytes = job.Checkpoint.Segments.Sum(s => s.SavedOffset),
            Parts = GetPartProgress(job.Checkpoint)
        };
    }

    private void SaveCheckpoint(Job job)
    {
        if (job.Checkpoint is null)
        {
            return;
        }
        try
        {
            var dir = TempDirectory(job);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "metadata.json");
            var pending = path + ".tmp";
            lock (job.CheckpointSync)
            {
                using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, job.Checkpoint);
                    stream.Flush(true);
                }
                File.Move(pending, path, true);
            }
        }
        catch (IOException) { }
    }

    private static string TempDirectory(Job job) => Path.Combine(job.Snapshot.DestinationDirectory, $".litedm-{job.Snapshot.Id:N}");
    private static void DeleteOwnedTemp(Job job)
    {
        try
        {
            DeleteTempDirectory(job);
        }
        catch (IOException) { }
    }

    private static void DeleteTempDirectory(Job job)
    {
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.Snapshot.DestinationDirectory));
        var directory = Path.GetFullPath(TempDirectory(job));
        if (!string.Equals(Path.GetDirectoryName(directory), destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The partial download folder is outside its destination.");
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }
    private Job Find(Guid id)
    {
        lock (_sync)
        {
            return _jobs.TryGetValue(id, out var j) ? j : throw new KeyNotFoundException($"Download {id} was not found.");
        }
    }
    private void RemoveQueuedId(Guid id)
    {
        if (_queue.Count == 0)
        {
            return;
        }
        var retained = _queue.Where(queuedId => queuedId != id).ToArray();
        _queue.Clear();
        foreach (var queuedId in retained)
        {
            _queue.Enqueue(queuedId);
        }
    }
    private void Publish(DownloadSnapshot snapshot)
    {
        var handlers = onChangedCallback;
        if (handlers is null)
        {
            return;
        }
        foreach (Action<DownloadSnapshot> h in handlers.GetInvocationList())
        {
            try
            {
                h(snapshot);
            }
            catch { }
        }
    }
    private void ThrowIfShutdown()
    {
        if (_shutdown)
        {
            throw new ObjectDisposedException(nameof(DownloadManager));
        }
    }
    private static bool IsTerminal(DownloadState state) => state is DownloadState.Completed or DownloadState.Cancelled;
    private static void ValidateOptions(DownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxConcurrentDownloads < 1 || options.SegmentsPerDownload < 1 || options.MinimumSegmentedSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private sealed class Job(DownloadSnapshot snapshot)
    {
        public DownloadSnapshot Snapshot = snapshot;
        public Checkpoint? Checkpoint;
        public CancellationTokenSource? Cancellation;
        public Task? Running;
        public readonly SemaphoreSlim Lifecycle = new(1, 1);
        public bool PauseRequested;
        public bool CancelRequested;
        public object CheckpointSync { get; } = new();
        public long StartTimestamp = Stopwatch.GetTimestamp();
        public long StartDownloadedBytes;
    }

    private sealed class Checkpoint
    {
        public string Url { get; set; } = "";
        public string FileName { get; set; } = "";
        public string DestinationDirectory { get; set; } = "";
        public long? Length { get; set; }
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public bool Segmented { get; set; }
        public bool CanResume { get; set; }
        public List<Segment> Segments { get; set; } = [];
    }

    private sealed class Segment
    {
        public long From { get; set; }
        public long To { get; set; }
        public string Path { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore]
        public long Offset { get; set; }
        public long SavedOffset { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public long CurrentRequestBytes { get; set; }
    }

    private sealed class ResourceChangedException(string message) : IOException(message);
    private sealed class TransientNetworkException(string message, Exception inner) : IOException(message, inner);
}
