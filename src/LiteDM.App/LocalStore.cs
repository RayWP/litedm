using System.Text.Json;
using LiteDM.Core;
using Microsoft.Data.Sqlite;

namespace LiteDM.App;

internal sealed record AppSettings
{
    public string Theme { get; init; } = "light";
    public int MaxConcurrentDownloads { get; init; } = 1;
    public int SegmentsPerDownload { get; init; } = 4;
    public DownloadOptions ToOptions() => new() { MaxConcurrentDownloads = MaxConcurrentDownloads, SegmentsPerDownload = SegmentsPerDownload };
}

internal sealed record PersistedData
{
    public int Version { get; init; } = 1;
    public AppSettings Settings { get; init; } = new();
    public List<DownloadSnapshot> History { get; init; } = [];
}

internal sealed record LoadResult(AppSettings Settings, List<DownloadSnapshot> Snapshots, string? Warning);

internal sealed class LocalStore
{
    public const int HistoryLimit = 20;
    private readonly string _path;
    private readonly string _legacyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public AppSettings Settings { get; set; } = new();

    public LocalStore(string? path = null, string? legacyPath = null)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteDM");
        _path = Path.GetFullPath(path ?? Path.Combine(folder, "state.db"));
        _legacyPath = legacyPath ?? Path.Combine(Path.GetDirectoryName(_path)!, "state.json");
    }

    public async Task<LoadResult> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var result = await Task.Run(Load);
            Settings = result.Settings;
            return result;
        }
        finally { _gate.Release(); }
    }

    private LoadResult Load()
    {
        try
        {
            using var connection = OpenDatabase();
            using var settingsCommand = connection.CreateCommand();
            settingsCommand.CommandText = "SELECT snapshot FROM settings WHERE id = 1";
            if (settingsCommand.ExecuteScalar() is not string settingsJson)
            {
                if (!File.Exists(_legacyPath)) return new(new AppSettings(), [], null);
                var legacy = JsonSerializer.Deserialize<PersistedData>(File.ReadAllText(_legacyPath), Json);
                if (legacy is null || legacy.Version != 1)
                    throw new InvalidDataException("The saved LiteDM state has an unsupported format.");
                var importedSettings = ValidateSettings(legacy.Settings ?? new AppSettings());
                var importedJobs = KeepRecentHistory(legacy.History ?? []);
                Write(connection, importedJobs, importedSettings);
                return new(importedSettings, importedJobs, null);
            }

            var settings = ValidateSettings(JsonSerializer.Deserialize<AppSettings>(settingsJson, Json)
                ?? throw new InvalidDataException("The saved settings are missing."));
            using var jobsCommand = connection.CreateCommand();
            jobsCommand.CommandText = "SELECT snapshot FROM downloads ORDER BY position";
            using var reader = jobsCommand.ExecuteReader();
            List<DownloadSnapshot> jobs = [];
            while (reader.Read())
            {
                if (JsonSerializer.Deserialize<DownloadSnapshot>(reader.GetString(0), Json) is { } job)
                    jobs.Add(job);
            }
            return new(settings, jobs, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or SqliteException)
        {
            return new(new AppSettings(), [], $"Saved downloads could not be loaded. {ex.Message}");
        }
    }

    public async Task SaveAsync(IEnumerable<DownloadSnapshot> snapshots, AppSettings settings)
    {
        var jobs = KeepRecentHistory(snapshots.ToList());
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                using var connection = OpenDatabase();
                Write(connection, jobs, settings);
            });
        }
        finally { _gate.Release(); }
    }

    private SqliteConnection OpenDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY CHECK (id = 1), snapshot TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS downloads (position INTEGER PRIMARY KEY, snapshot TEXT NOT NULL);
                """;
            schema.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Write(SqliteConnection connection, IReadOnlyList<DownloadSnapshot> jobs, AppSettings settings)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO settings (id, snapshot) VALUES (1, $settings); DELETE FROM downloads;";
        command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(settings, Json));
        command.ExecuteNonQuery();

        command.Parameters.Clear();
        command.CommandText = "INSERT INTO downloads (position, snapshot) VALUES ($position, $snapshot)";
        var position = command.Parameters.Add("$position", SqliteType.Integer);
        var snapshot = command.Parameters.Add("$snapshot", SqliteType.Text);
        for (var i = 0; i < jobs.Count; i++)
        {
            position.Value = i;
            snapshot.Value = JsonSerializer.Serialize(jobs[i], Json);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static AppSettings ValidateSettings(AppSettings settings)
    {
        if (settings.Theme is not ("system" or "light" or "dark") || settings.MaxConcurrentDownloads is < 1 or > 16 || settings.SegmentsPerDownload is < 1 or > 16)
            throw new InvalidDataException("Saved LiteDM settings contain values outside the supported range.");
        return settings;
    }

    public static IEnumerable<DownloadSnapshot> RecentHistory(IEnumerable<DownloadSnapshot> snapshots, string search = "", DownloadHistoryStatus? status = null) => snapshots
        .Where(job => job.HistoryStatus.HasValue)
        .Reverse()
        .OrderByDescending(job => job.HistoryAtUtc)
        .Take(HistoryLimit)
        .Where(job => (!status.HasValue || job.HistoryStatus == status)
            && job.FileName.Contains(search, StringComparison.OrdinalIgnoreCase));

    private static List<DownloadSnapshot> KeepRecentHistory(List<DownloadSnapshot> snapshots)
    {
        var finished = RecentHistory(snapshots.Where(job => job.State == DownloadState.Completed))
            .Select(job => job.Id).ToHashSet();
        return snapshots.Where(job => job.State != DownloadState.Cancelled
            && (job.State != DownloadState.Completed || finished.Contains(job.Id))).ToList();
    }
}
