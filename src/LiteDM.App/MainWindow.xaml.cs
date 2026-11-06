using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LiteDM.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LiteDM.App;

public sealed partial class MainWindow : Window
{
    private readonly LocalStore _store = new();
    private readonly Dictionary<Guid, DownloadSnapshot> _jobs = [];
    private readonly Dictionary<Guid, JobCard> _cards = [];
    private DownloadSnapshot[] _visibleHistory = [];
    private DownloadManager? _manager;
    private TrayIcon? _tray;
    private bool _allowClose;
    private bool _initialized;
    private Task _requestQueue = Task.CompletedTask;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly CancellationTokenSource _pipeLifetime = new();
    private Task _pipeTask = Task.CompletedTask;
    private Task _removalTask = Task.CompletedTask;
    private bool _saveDirty;
    private bool _saveInProgress;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = false;
        var appWindow = AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(900, 620));
        UpdateMinimumWindowSize();
        Root.Loaded += (_, _) => Root.XamlRoot.Changed += (_, _) => UpdateMinimumWindowSize();
        appWindow.Closing += OnClosing;
        Root.ActualThemeChanged += (_, _) =>
        {
            if (_store.Settings.Theme == "system")
            {
                ApplyTheme("system");
            }
        };
        _tray = new TrayIcon(WindowNative.GetWindowHandle(this), ShowWindow, ExitFromTray);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        var loaded = await _store.LoadAsync();
        ApplyTheme(loaded.Settings.Theme);
        _manager = new DownloadManager(loaded.Settings.ToOptions());
        _manager.onChangedCallback += OnChanged;
        if (loaded.Snapshots.Count > 0)
        {
            foreach (var snapshot in loaded.Snapshots)
            {
                _jobs[snapshot.Id] = snapshot;
            }
            RenderJobs();
            try
            {
                StartupLog.Write("Restore started");
                await _manager.RestoreAsync(loaded.Snapshots);
                StartupLog.Write("Restore completed");
            }
            catch (Exception ex)
            {
                ShowNotice($"Saved downloads could not be restored: {ex.Message}", isError: true);
            }
        }
        if (loaded.Warning is not null)
        {
            ShowNotice(loaded.Warning, isError: true);
        }
        RenderJobs();
        _pipeTask = ServePipeAsync(_pipeLifetime.Token);
    }

    private void OnChanged(DownloadSnapshot snapshot)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_jobs.ContainsKey(snapshot.Id) && _manager?.GetJobs().Any(job => job.Id == snapshot.Id) != true)
            {
                return;
            }
            _jobs[snapshot.Id] = snapshot;
            RenderJobs();
            ScheduleSave();
        });
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await ReviewUrlAsync(UrlBox.Text);
    private async void UrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await ReviewUrlAsync(UrlBox.Text);
        }
    }

    private async Task<bool> ReviewUrlAsync(string? rawUrl)
    {
        await _dialogGate.WaitAsync();
        try
        {
            return await ReviewUrlCoreAsync(rawUrl);
        }
        catch (Exception ex)
        {
            ShowNotice($"The download could not be reviewed: {ex.Message}", true);
            return false;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task<bool> ReviewUrlCoreAsync(string? rawUrl)
    {
        var url = rawUrl?.Trim() ?? "";
        if (!NativeProtocol.IsSupportedUrl(url))
        {
            ShowNotice("Enter a valid HTTP or HTTPS link.", true);
            return false;
        }
        var uri = new Uri(url);
        var suggested = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(suggested))
        {
            suggested = "download";
        }
        var nameBox = new TextBox { Text = suggested, Header = "File name" };
        var pathBox = new TextBox { Text = WindowsFolderPaths.GetDownloads(), Header = "Save location" };
        var browse = new Button { Content = "Choose folder…", VerticalAlignment = VerticalAlignment.Bottom };
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["Danger"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(pathBox);
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(browse);
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null)
                {
                    pathBox.Text = folder.Path;
                }
            }
            catch (Exception ex)
            {
                error.Text = $"The folder picker could not be opened. {ex.Message}";
                error.Visibility = Visibility.Visible;
            }
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = url, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Foreground = (Brush)Application.Current.Resources["SecondaryText"] });
        content.Children.Add(nameBox);
        content.Children.Add(folderRow);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "Review download",
            Content = content,
            PrimaryButtonText = "Download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text) && Path.IsPathFullyQualified(pathBox.Text.Trim()))
            {
                return;
            }
            args.Cancel = true;
            error.Text = "Enter a file name and choose a folder or type its full path.";
            error.Visibility = Visibility.Visible;
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return false;
        }
        try
        {
            var job = await _manager!.AddAsync(url, pathBox.Text.Trim(), nameBox.Text.Trim());
            _jobs[job.Id] = job;
            RenderJobs();
            await _store.SaveAsync(_jobs.Values.ToArray(), _store.Settings);
            UrlBox.Text = "";
            ShowNotice("Download added to the queue.");
            return true;
        }
        catch (Exception ex)
        {
            ShowNotice($"Could not add download: {ex.Message}", true);
            return false;
        }
    }

    private void RenderJobs()
    {
        var transfers = _jobs.Values.Where(job => job.State != DownloadState.Completed).ToArray();
        RemoveStoppedButton.IsEnabled = !_allowClose && transfers.Any(job => job.CanRemove);
        EmptyPanel.Visibility = transfers.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var stale in _cards.Keys.Except(transfers.Select(job => job.Id)).ToArray())
        {
            JobsPanel.Children.Remove(_cards[stale].Border);
            _cards.Remove(stale);
        }
        foreach (var job in transfers)
        {
            if (!_cards.TryGetValue(job.Id, out var card))
            {
                card = CreateJobCard(job);
                _cards[job.Id] = card;
                JobsPanel.Children.Add(card.Border);
            }
            UpdateJobCard(card, job);
        }
        FooterText.Text = transfers.Length == 0 ? "Ready" : $"{transfers.Length} transfer{(transfers.Length == 1 ? "" : "s")}";
        RenderHistory();
    }

    private void HistorySearch_Changed(object sender, TextChangedEventArgs e) => RenderHistory();

    private void HistoryFilter_Changed(object sender, SelectionChangedEventArgs e) => RenderHistory();

    private void RenderHistory()
    {
        if (HistoryEntries is null || HistoryEmpty is null || HistorySearch is null || HistoryFilter is null)
        {
            return;
        }
        var selected = (HistoryFilter.SelectedItem as ComboBoxItem)?.Content as string;
        DownloadHistoryStatus? status = Enum.TryParse<DownloadHistoryStatus>(selected, out var value) ? value : null;
        var search = HistorySearch.Text.Trim();
        var history = LocalStore.RecentHistory(_jobs.Values, search, status).ToArray();
        HistoryEmpty.Visibility = history.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Text = search.Length > 0 || status.HasValue ? "No matching downloads" : "No history yet";
        if (_visibleHistory.SequenceEqual(history))
        {
            return;
        }

        _visibleHistory = history;
        HistoryEntries.Children.Clear();
        foreach (var job in history)
        {
            var name = new TextBlock
            {
                Text = job.FileName,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["PrimaryText"]
            };
            var details = new TextBlock
            {
                Text = job.HistoryStatus.ToString(),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["SecondaryText"]
            };
            var entry = new StackPanel { Padding = new Thickness(7, 4, 7, 4), Spacing = 2, Children = { name, details } };
            ToolTipService.SetToolTip(entry, job.FullPath);
            HistoryEntries.Children.Add(entry);
        }
    }

    private JobCard CreateJobCard(DownloadSnapshot job)
    {
        var fileName = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["PrimaryText"], TextTrimming = TextTrimming.CharacterEllipsis };
        var directory = new TextBlock { FontSize = 11, Foreground = (Brush)Application.Current.Resources["SecondaryText"], TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { Foreground = (Brush)Application.Current.Resources["PrimaryText"], TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5 };
        var progressText = new TextBlock { FontSize = 11, Foreground = (Brush)Application.Current.Resources["SecondaryText"] };
        var etaText = new TextBlock { FontSize = 10, Foreground = (Brush)Application.Current.Resources["SecondaryText"] };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        var partGrid = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
        partGrid.ColumnDefinitions.Add(new ColumnDefinition());
        partGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var partHeading = new TextBlock
        {
            Text = "Download parts",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["PrimaryText"]
        };
        var parts = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed, Children = { partHeading, partGrid } };
        var info = new StackPanel { Spacing = 5, Children = { fileName, directory, status, progress, progressText, etaText, parts, controls } };
        var border = new Border { Padding = new Thickness(12, 10, 12, 10), Background = (Brush)Application.Current.Resources["Surface"], CornerRadius = new CornerRadius(4), Child = info };
        return new JobCard(border, fileName, directory, status, progress, progressText, etaText, controls, parts, partGrid);
    }

    private void UpdateJobCard(JobCard card, DownloadSnapshot job)
    {
        card.FileName.Text = job.FileName;
        card.Directory.Text = job.DestinationDirectory;
        card.Status.Text = StateLabel(job) + (job.Error is null ? "" : $": {job.Error}");
        card.Progress.Value = job.TotalBytes is > 0 ? Math.Clamp(job.DownloadedBytes * 100d / job.TotalBytes.Value, 0, 100) : job.State == DownloadState.Completed ? 100 : 0;
        card.Progress.IsIndeterminate = job.State == DownloadState.Downloading && job.TotalBytes is null;
        card.ProgressText.Text = $"{FormatBytes(job.DownloadedBytes)} / {(job.TotalBytes is long total ? FormatBytes(total) : "unknown size")}  |  {TransferText(job)}";
        card.Eta.Text = job.State == DownloadState.Downloading && job.TotalBytes is long totalBytes && job.BytesPerSecond > 0
            ? $"About {TimeSpan.FromSeconds(Math.Max(0, (totalBytes - job.DownloadedBytes) / job.BytesPerSecond)):h\\:mm\\:ss} remaining" : "";
        ToolTipService.SetToolTip(card.Border, job.Error);
        UpdatePartBars(card, job);
        var mode = ControlMode(job);
        if (card.Mode == mode)
        {
            return;
        }
        card.Mode = mode;
        card.Controls.Children.Clear();
        if (mode == "active")
        {
            card.Controls.Children.Add(ActionButton("Pause", job, async id => await _manager!.PauseAsync(id)));
        }
        if (mode == "resume")
        {
            card.Controls.Children.Add(ActionButton("Resume", job, async id => await _manager!.ResumeAsync(id)));
        }
        if (mode == "retry")
        {
            card.Controls.Children.Add(ActionButton("Retry", job, async id => await _manager!.ResumeAsync(id)));
        }
        if (mode == "restart")
        {
            card.Controls.Children.Add(ActionButton("Restart", job, async id => await _manager!.RestartAsync(id)));
        }
        if (mode == "active")
        {
            card.Controls.Children.Add(ActionButton("Cancel", job, id => _manager!.CancelAsync(id)));
        }
        if (job.CanRemove)
        {
            card.Controls.Children.Add(ActionButton("Remove", job, id => ConfirmRemovalAsync([id])));
        }
    }

    private async void RemoveStopped_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ConfirmRemovalAsync(_jobs.Values.Where(job => job.CanRemove).Select(job => job.Id).ToArray());
        }
        catch (Exception ex)
        {
            ShowNotice($"Could not remove stopped transfers. {ex.Message}", true);
        }
    }

    private async Task ConfirmRemovalAsync(Guid[] ids)
    {
        await _dialogGate.WaitAsync();
        try
        {
            var manager = _manager;
            var jobs = ids.Where(id => _jobs.TryGetValue(id, out var job) && job.CanRemove).ToArray();
            if (_allowClose || manager is null || jobs.Length == 0)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = jobs.Length == 1 ? "Remove transfer?" : $"Remove {jobs.Length} stopped transfers?",
                Content = "This clears the selected transfers and deletes their partial files.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Keep transfers",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Root.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _allowClose)
            {
                return;
            }
            await (_removalTask = RemoveConfirmedAsync(manager, jobs));
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task RemoveConfirmedAsync(DownloadManager manager, Guid[] jobs)
    {
        var removed = 0;
        var failed = 0;
        string? failure = null;
        foreach (var id in jobs)
        {
            try
            {
                if (!await manager.RemoveStoppedAsync(id))
                {
                    continue;
                }
                _jobs.Remove(id);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                failure ??= ex.Message;
            }
        }

        RenderJobs();
        if (removed > 0)
        {
            try
            {
                await _store.SaveAsync(_jobs.Values.ToArray(), _store.Settings);
            }
            catch
            {
                ScheduleSave();
                throw;
            }
        }

        if (failed > 0)
        {
            ShowNotice($"Removed {removed}. Could not remove {failed}. {failure}", true);
        }
        else if (removed > 0)
        {
            ShowNotice(removed == 1 ? "Transfer removed." : $"{removed} transfers removed.");
        }
    }

    private static void UpdatePartBars(JobCard card, DownloadSnapshot job)
    {
        var count = job.Parts.IsDefault ? 0 : job.Parts.Length;
        card.Parts.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (card.PartBars.Count != count)
        {
            card.PartBars.Clear();
            card.PartGrid.Children.Clear();
            card.PartGrid.RowDefinitions.Clear();
            for (var i = 0; i < count; i++)
            {
                if (i % 2 == 0)
                {
                    card.PartGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }
                var label = new TextBlock { FontSize = 12, Foreground = (Brush)Application.Current.Resources["SecondaryText"] };
                var bar = new ProgressBar { Maximum = 100, Height = 5, Foreground = (Brush)Application.Current.Resources["Accent"] };
                AutomationProperties.SetName(bar, $"Part {i + 1} progress");
                var cell = new StackPanel { Spacing = 2, Children = { label, bar } };
                Grid.SetRow(cell, i / 2);
                Grid.SetColumn(cell, i % 2);
                card.PartGrid.Children.Add(cell);
                card.PartBars.Add((label, bar));
            }
        }
        for (var i = 0; i < count; i++)
        {
            var part = job.Parts[i];
            var percent = part.TotalBytes > 0 ? Math.Clamp(100d * part.DownloadedBytes / part.TotalBytes, 0, 100) : 0;
            var (label, bar) = card.PartBars[i];
            label.Text = $"Part {i + 1} · {percent:0.#}%";
            bar.Value = percent;
        }
    }

    private static string ControlMode(DownloadSnapshot job) => job.State switch
    {
        DownloadState.Downloading or DownloadState.Queued => "active",
        DownloadState.Paused when !job.RequiresRestart => "resume",
        DownloadState.Failed when !job.RequiresRestart => "retry",
        DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled => "restart",
        _ => "none"
    };

    private void UpdateMinimumWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d;
        var width = (int)Math.Ceiling(656 * scale);
        var height = (int)Math.Ceiling(520 * scale);
        if (presenter.PreferredMinimumWidth != width)
        {
            presenter.PreferredMinimumWidth = width;
        }
        if (presenter.PreferredMinimumHeight != height)
        {
            presenter.PreferredMinimumHeight = height;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    private sealed class JobCard(Border border, TextBlock fileName, TextBlock directory, TextBlock status, ProgressBar progress, TextBlock progressText, TextBlock eta, StackPanel controls, StackPanel parts, Grid partGrid)
    {
        public Border Border { get; } = border;
        public TextBlock FileName { get; } = fileName;
        public TextBlock Directory { get; } = directory;
        public TextBlock Status { get; } = status;
        public ProgressBar Progress { get; } = progress;
        public TextBlock ProgressText { get; } = progressText;
        public TextBlock Eta { get; } = eta;
        public StackPanel Controls { get; } = controls;
        public string? Mode { get; set; }
        public StackPanel Parts { get; } = parts;
        public Grid PartGrid { get; } = partGrid;
        public List<(TextBlock Label, ProgressBar Bar)> PartBars { get; } = [];
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
    private void ScheduleSave()
    {
        _saveDirty = true;
        if (_saveTimer is null)
        {
            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromSeconds(1);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += async (_, _) => await FlushPendingSaveAsync();
        }
        if (!_saveTimer.IsRunning && !_saveInProgress)
        {
            _saveTimer.Start();
        }
    }

    private async Task FlushPendingSaveAsync()
    {
        if (!_saveDirty || _saveInProgress)
        {
            return;
        }
        _saveDirty = false;
        _saveInProgress = true;
        try
        {
            await _store.SaveAsync(_jobs.Values.ToArray(), _store.Settings);
        }
        catch (Exception ex)
        {
            ShowNotice($"Download progress could not be saved: {ex.Message}", true);
        }
        finally
        {
            _saveInProgress = false;
            if (_saveDirty)
            {
                _saveTimer?.Start();
            }
        }
    }

    private Button ActionButton(string label, DownloadSnapshot job, Func<Guid, Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(7, 5, 7, 5), MinWidth = 58 };
        AutomationProperties.SetName(button, $"{label} {job.FileName}");
        button.Click += async (_, _) =>
{
    button.IsEnabled = false;
    try
    {
        await action(job.Id);
    }
    catch (Exception ex)
    {
        ShowNotice($"{label} failed: {ex.Message}", true);
    }
    finally
    {
        button.IsEnabled = true;
    }
};
        return button;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await _dialogGate.WaitAsync();
        try
        {
            await ShowSettingsDialogAsync();
        }
        catch (Exception ex)
        {
            ShowNotice($"Settings could not be opened: {ex.Message}", true);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task ShowSettingsDialogAsync()
    {
        var current = _store.Settings;
        var max = new NumberBox { Header = "Maximum simultaneous downloads", Value = current.MaxConcurrentDownloads, Minimum = 1, Maximum = 16, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var segments = new NumberBox { Header = "Segments per download", Value = current.SegmentsPerDownload, Minimum = 1, Maximum = 16, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var theme = new ComboBox { Header = "Appearance", SelectedIndex = current.Theme switch { "light" => 1, "dark" => 2, _ => 0 }, MinWidth = 220 };
        theme.Items.Add(new ComboBoxItem { Content = "System" });
        theme.Items.Add(new ComboBoxItem { Content = "Light" });
        theme.Items.Add(new ComboBoxItem { Content = "Dark" });
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(max);
        content.Children.Add(segments);
        content.Children.Add(theme);
        var dialog = new ContentDialog { Title = "Settings", Content = content, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Root.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (!double.IsFinite(max.Value) || !double.IsFinite(segments.Value) || max.Value != Math.Truncate(max.Value) || segments.Value != Math.Truncate(segments.Value))
        {
            ShowNotice("Enter whole numbers between 1 and 16 for the download limits.", true);
            return;
        }
        var updated = current with
        {
            MaxConcurrentDownloads = (int)Math.Clamp(max.Value, 1, 16),
            SegmentsPerDownload = (int)Math.Clamp(segments.Value, 1, 16),
            Theme = theme.SelectedIndex switch
            {
                1 => "light",
                2 => "dark",
                _ => "system"
            }
        };
        _store.Settings = updated;
        _manager?.UpdateOptions(updated.ToOptions());
        ApplyTheme(updated.Theme);
        try
        {
            await _store.SaveAsync(_jobs.Values.ToArray(), updated);
            ShowNotice("Settings saved.");
        }
        catch (Exception ex)
        {
            ShowNotice($"Settings were applied but could not be saved: {ex.Message}", true);
        }
    }

    private void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        var dark = theme == "dark" || theme == "system" && Root.ActualTheme == ElementTheme.Dark;
        var colors = dark
            ? new Dictionary<string, string>
            {
                ["AppBackground"] = "#172431",
                ["Surface"] = "#223344",
                ["SurfaceAlt"] = "#2B4053",
                ["PrimaryText"] = "#E8F1F8",
                ["SecondaryText"] = "#B6C7D6",
                ["Accent"] = "#75B5F1",
                ["AccentForeground"] = "#17324D",
                ["Line"] = "#53697D",
                ["Danger"] = "#FF9C91"
            }
            : new Dictionary<string, string>
            {
                ["AppBackground"] = "#E8EDF2",
                ["Surface"] = "#FFFFFF",
                ["SurfaceAlt"] = "#DCEFFC",
                ["PrimaryText"] = "#17324D",
                ["SecondaryText"] = "#50677D",
                ["Accent"] = "#2563A6",
                ["AccentForeground"] = "#FFFFFF",
                ["Line"] = "#CBD6E0",
                ["Danger"] = "#A63A3A"
            };
        foreach (var (key, value) in colors)
        {
            var brush = (SolidColorBrush)Application.Current.Resources[key];
            var c = Convert.ToUInt32(value[1..], 16);
            brush.Color = Windows.UI.Color.FromArgb(255, (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        }
    }
    private void ShowNotice(string text, bool isError = false)
    {
        NoticeText.Text = text;
        NoticeBorder.Visibility = Visibility.Visible;
        NoticeBorder.Background = (Brush)Application.Current.Resources[isError ? "SurfaceAlt" : "Surface"];
        NoticeText.Foreground = (Brush)Application.Current.Resources[isError ? "Danger" : "PrimaryText"];
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            if (_tray?.Available == true)
            {
                args.Cancel = true;
                HideToTray();
            }
            else
            {
                args.Cancel = true;
                ShowNotice("LiteDM could not create its tray icon, so the window stayed open. Try again after the notification area is available.", true);
            }
        }
    }
    private void HideToTray() => AppWindow.Hide();
    private void ShowWindow()
    {
        AppWindow.Show();
        Activate();
    }
    private async Task ExitFromTray()
    {
        if (_allowClose)
        {
            return;
        }
        _allowClose = true;
        AddButton.IsEnabled = SettingsButton.IsEnabled = false;
        RemoveStoppedButton.IsEnabled = false;
        try
        {
            await _removalTask;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        _saveTimer?.Stop();
        _pipeLifetime.Cancel();
        await Task.WhenAny(_pipeTask, Task.Delay(1000));
        try
        {
            if (_manager is not null)
            {
                await _manager.ShutdownAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        try
        {
            await _store.SaveAsync(_manager?.GetJobs() ?? _jobs.Values.ToArray(), _store.Settings);
        }
        catch (Exception ex)
        {
            ShowNotice($"Final progress could not be saved: {ex.Message} Downloads are stopped. Use Exit in the tray to retry saving.", true);
            ShowWindow();
            foreach (var card in _cards.Values)
            {
                foreach (var control in card.Controls.Children.OfType<Control>())
                {
                    control.IsEnabled = false;
                }
            }
            UrlBox.IsEnabled = false;
            _allowClose = false;
            return;
        }
        if (_manager is not null)
        {
            await _manager.DisposeAsync();
        }
        _tray?.Dispose();
        Close();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private async Task ServePipeAsync(CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(NativeProtocol.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(lifetime);
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await ReadBoundedLineAsync(server, 64 * 1024, connectionTimeout.Token);
                NativeResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<NativeRequest>(line ?? "", NativeProtocol.JsonOptions);
                    if (request?.Action == "show")
                    {
                        await DispatcherTask(ShowWindow);
                        response = new(true);
                    }
                    else if (request?.Action == "addDownload" && NativeProtocol.IsSupportedUrl(request.Url))
                    {
                        _requestQueue = _requestQueue.ContinueWith(async _ =>
                        {
                            try
                            {
                                await DispatcherTask(ShowWindow);
                                await DispatcherTask(() => ReviewUrlAsync(request.Url));
                            }
                            catch (Exception ex)
                            {
                                await DispatcherTask(() => ShowNotice($"Could not review the requested download: {ex.Message}", true));
                            }
                        }, TaskScheduler.Default).Unwrap();
                        await DispatcherTask(ShowWindow);
                        response = new(true);
                    }
                    else
                    {
                        response = new(false, "Unsupported request.");
                    }
                }
                catch (Exception ex)
                {
                    response = new(false, ex.Message);
                }
                var encoded = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, NativeProtocol.JsonOptions) + "\n");
                await server.WriteAsync(encoded, connectionTimeout.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Pipe server: {ex.Message}");
                if (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(300, lifetime);
                }
            }
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var one = new byte[1];
        while (memory.Length <= maximumBytes)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return memory.Length == 0 ? null : new UTF8Encoding(false, true).GetString(memory.ToArray());
            }
            if (one[0] == (byte)'\n')
            {
                return new UTF8Encoding(false, true).GetString(memory.ToArray()).TrimEnd('\r');
            }
            memory.WriteByte(one[0]);
        }
        throw new InvalidDataException("The local request exceeded the 64 KiB limit.");
    }

    private Task DispatcherTask(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }
    private Task DispatcherTask(Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private static string StateLabel(DownloadSnapshot x) => x.State switch
    {
        DownloadState.Queued => "Queued",
        DownloadState.Downloading => "Downloading",
        DownloadState.Paused => "Paused",
        DownloadState.Completed => "Finish",
        DownloadState.Cancelled => "Cancelled",
        _ => x.IsCorrupt ? "Corrupt" : "Failed"
    };
    private static string TransferText(DownloadSnapshot x) => x.State == DownloadState.Downloading ? $"{FormatBytes((long)x.BytesPerSecond)}/s" : x.State == DownloadState.Failed ? (x.Error ?? "Failed") : x.State.ToString();
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{(long)value} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
