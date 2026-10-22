using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using YoutubeToMp4.Models;
using YoutubeToMp4.Services;

namespace YoutubeToMp4.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    static readonly Brush Green = Frozen("#7FD3A0");
    static readonly Brush Gold = Frozen("#E8C97A");
    static readonly Brush Orange = Frozen("#FF8A5C");
    static readonly Brush BorderNormal = Frozen("#2A3646");
    static readonly Brush BorderGold = Frozen("#5A4D2A");
    static readonly Brush BorderOrange = Frozen("#6A3B28");

    readonly AppSettings _settings = AppSettings.Load();
    readonly ToolManager _tools = new();
    readonly UsageTracker _usage = new();
    readonly YtDlpClient _yt;
    readonly DownloadScheduler _scheduler;
    readonly System.Windows.Threading.DispatcherTimer _timer;
    readonly CancellationTokenSource _lifetime = new();

    string _url = "";
    string _footer = "Starting up...";
    bool _footerIsError;
    bool _isAdding;
    bool _toolsBusy;
    PacePreset _preset;

    public MainViewModel()
    {
        _yt = new YtDlpClient(_tools, _settings);
        _scheduler = new DownloadScheduler(_settings, _usage, _yt);
        _preset = PacePreset.Get(_settings.PaceKey);

        AddCommand = new RelayCommand(() => _ = AddAsync(), () => !_isAdding);
        PauseCommand = new RelayCommand(TogglePause);
        RetryCommand = new RelayCommand(RetryFailed, () => HasFailed);
        CancelAllCommand = new RelayCommand(CancelAll, () => Items.Any(i => i.Status != ItemStatus.Done));
        ChangeFolderCommand = new RelayCommand(ChangeFolder);

        _scheduler.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        _scheduler.Start();

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        _ = SetUpToolsAsync();
    }

    public ObservableCollection<QueueItem> Items { get; } = new();
    public IReadOnlyList<PacePreset> Presets => PacePreset.All;

    public IReadOnlyList<string> Browsers { get; } =
        new[] { "firefox", "edge", "chrome", "brave", "opera", "vivaldi" };

    public IReadOnlyList<int> Heights { get; } = new[] { 360, 480, 720, 1080, 1440, 2160 };

    public ICommand AddCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CancelAllCommand { get; }
    public ICommand ChangeFolderCommand { get; }

    // --- Input -----------------------------------------------------------------------------

    public string Url
    {
        get => _url;
        set => Set(ref _url, value);
    }

    // --- Settings the UI edits --------------------------------------------------------------

    public PacePreset SelectedPreset
    {
        get => _preset;
        set
        {
            if (value == null || !Set(ref _preset, value)) return;
            _settings.PaceKey = value.Key;
            _settings.Save();
            Refresh();
        }
    }

    public bool UseBrowserCookies
    {
        get => _settings.UseBrowserCookies;
        set { _settings.UseBrowserCookies = value; _settings.Save(); Raise(); }
    }

    public bool RandomizeWaits
    {
        get => _settings.RandomizeWaits;
        set { _settings.RandomizeWaits = value; _settings.Save(); Raise(); }
    }

    public bool PauseWhenBlocked
    {
        get => _settings.PauseWhenBlocked;
        set { _settings.PauseWhenBlocked = value; _settings.Save(); Raise(); }
    }

    public string CookieBrowser
    {
        get => _settings.CookieBrowser;
        set { if (value == null) return; _settings.CookieBrowser = value; _settings.Save(); Raise(); }
    }

    public int MaxHeight
    {
        get => _settings.MaxHeight;
        set { _settings.MaxHeight = value; _settings.Save(); Raise(); Raise(nameof(QualityText)); }
    }

    public string OutputDir => _settings.OutputDir;
    public string QualityText => $"Up to {_settings.MaxHeight}p";

    // --- Queue ------------------------------------------------------------------------------

    public bool HasFailed => Items.Any(i => i.Status == ItemStatus.Failed);

    public string QueueSummary
    {
        get
        {
            var done = Items.Count(i => i.Status == ItemStatus.Done);
            var failed = Items.Count(i => i.Status == ItemStatus.Failed);
            var left = Items.Count - done - failed;
            var text = $"{Items.Count} videos, {done} done, {left} to go";
            return failed > 0 ? text + $", {failed} failed" : text;
        }
    }

    public string PauseLabel => _scheduler.IsPaused ? "Resume" : "Pause all";
    public string PauseIcon => _scheduler.IsPaused ? "M8 5l11 7-11 7z" : "M6 5h4v14H6zM14 5h4v14h-4z";

    // --- Rate limit panel ---------------------------------------------------------------------

    public int UsedHour => _usage.UsedLastHour;
    public int UsedDay => _usage.UsedToday;
    public double HourPercent => Math.Min(100, UsedHour * 100.0 / _preset.PerHour);
    public double DayPercent => Math.Min(100, UsedDay * 100.0 / _preset.PerDay);

    public string Eta
    {
        get
        {
            var remaining = Items.Count(i => i.Status is ItemStatus.Queued or ItemStatus.Downloading
                or ItemStatus.Merging or ItemStatus.Paused);
            if (remaining == 0) return "--";

            var perVideo = Math.Max(_preset.AverageDelaySec, _scheduler.AverageDownloadSeconds / _preset.Threads);
            var minutes = (int)Math.Round(remaining * perVideo / 60);
            if (minutes < 1) return "under a minute";
            if (minutes < 120) return $"about {minutes} min";
            return $"about {minutes / 60.0:0.#} hours";
        }
    }

    public string HealthLabel => Risk switch
    {
        0 => "Block risk: Low",
        1 => "Block risk: Medium",
        _ => "Block risk: High"
    };

    public string HealthNote
    {
        get
        {
            if (_scheduler.BlockReason != null) return "A block was detected this session.";
            return Risk switch
            {
                0 => "Request rate is within limits. No warnings this session.",
                1 => "Over 50% of the hourly request cap is used.",
                _ => "Request rate may trigger a bot check or HTTP 429."
            };
        }
    }

    public Brush HealthBrush => Risk switch { 0 => Green, 1 => Gold, _ => Orange };
    public Brush HealthBorderBrush => Risk switch { 0 => BorderNormal, 1 => BorderGold, _ => BorderOrange };

    /// <summary>0 = low, 1 = medium, 2 = high.</summary>
    int Risk
    {
        get
        {
            var ratio = UsedHour / (double)_preset.PerHour;
            if (_scheduler.HadRecentBlock || _preset.Threads >= 4 || ratio >= 0.9) return 2;
            if (ratio > 0.5 || (_preset.Threads == 2 && ratio > 0.35)) return 1;
            return 0;
        }
    }

    // --- Alerts and footer ------------------------------------------------------------------------

    public bool HasAlert => _scheduler.BlockReason != null || _scheduler.HoldNotice != null;
    public string AlertText => _scheduler.BlockReason ?? _scheduler.HoldNotice ?? "";
    public Brush AlertBrush => _scheduler.BlockReason != null ? Orange : Gold;

    public string FooterText
    {
        get
        {
            if (_footerIsError || _toolsBusy || _isAdding) return _footer;
            if (_scheduler.IsPaused) return "Paused. Nothing is being requested.";
            if (Items.Count == 0) return "Paste a link to get started.";
            if (_scheduler.ActiveCount == 0 && _scheduler.PendingCount == 0)
                return HasFailed ? "Finished, with some failures." : "All done.";
            return "Downloading with " + _preset.Name;
        }
    }

    public Brush FooterBrush => _footerIsError ? Orange : Frozen("#9FB0C4");

    public string Countdown
    {
        get
        {
            if (_scheduler.IsPaused || _scheduler.PendingCount == 0) return "--";
            var left = _scheduler.NextStartUtc - DateTime.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"{(int)left.TotalMinutes}:{left.Seconds:00}";
        }
    }

    // --- Actions ------------------------------------------------------------------------------------

    async Task SetUpToolsAsync()
    {
        _toolsBusy = true;
        var progress = new Progress<string>(text => { _footer = text; _footerIsError = false; Refresh(); });
        try
        {
            await _tools.EnsureAsync(progress, _lifetime.Token);
            _scheduler.ToolsReady = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetError("Could not set up yt-dlp or ffmpeg: " + ex.Message);
        }
        finally
        {
            _toolsBusy = false;
            Refresh();
        }
    }

    async Task AddAsync()
    {
        var text = Url.Trim();
        if (text.Length == 0) return;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            !(uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)))
        {
            SetError("That does not look like a YouTube link.");
            return;
        }

        if (!_tools.Ready)
        {
            if (_toolsBusy) { SetError("Still setting up the download tools. Try again in a moment."); return; }
            await SetUpToolsAsync();
            if (!_tools.Ready) return;
            _scheduler.ToolsReady = true;
        }

        _isAdding = true;
        _footer = "Reading link...";
        _footerIsError = false;
        Refresh();

        try
        {
            var videos = await _yt.ResolveAsync(text, _lifetime.Token);
            var known = Items.Where(i => i.Status != ItemStatus.Failed).Select(i => i.VideoId).ToHashSet();
            var added = 0;
            foreach (var v in videos)
            {
                if (!known.Add(v.Id)) continue;
                var item = new QueueItem(v.Id, v.Title, v.DurationSeconds);
                Items.Add(item);
                _scheduler.Enqueue(item);
                added++;
            }

            Url = "";
            _footer = added == 0 ? "Those videos are already in the queue." : $"Added {added} video{(added == 1 ? "" : "s")}.";
            _footerIsError = false;
        }
        catch (ResolveException ex) when (ex.Blocked)
        {
            _scheduler.Pause("YouTube is asking for verification or limiting requests. Paused to protect your connection.");
            SetError(ex.Message);
        }
        catch (ResolveException ex)
        {
            SetError(ex.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetError("Could not read that link: " + ex.Message);
        }
        finally
        {
            _isAdding = false;
            Refresh();
        }
    }

    void TogglePause()
    {
        if (_scheduler.IsPaused) _scheduler.Resume();
        else _scheduler.Pause();
        Refresh();
    }

    void CancelAll()
    {
        foreach (var item in _scheduler.CancelAll()) Items.Remove(item);
        foreach (var item in Items.Where(i => i.Status is not ItemStatus.Done).ToList()) Items.Remove(item);
        Refresh();
    }

    void RetryFailed()
    {
        _scheduler.Requeue(Items.Where(i => i.Status == ItemStatus.Failed).ToList());
        Refresh();
    }

    void ChangeFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where videos are saved", InitialDirectory = _settings.OutputDir };
        if (dialog.ShowDialog() != true) return;
        _settings.OutputDir = dialog.FolderName;
        _settings.Save();
        Raise(nameof(OutputDir));
    }

    void SetError(string message)
    {
        _footer = message;
        _footerIsError = true;
        Refresh();
    }

    public void Shutdown()
    {
        _timer.Stop();
        _lifetime.Cancel();
        _scheduler.Dispose();
        _settings.Save();
    }

    void Refresh()
    {
        foreach (var name in new[]
        {
            nameof(HasFailed), nameof(QueueSummary), nameof(PauseLabel), nameof(PauseIcon),
            nameof(UsedHour), nameof(UsedDay), nameof(HourPercent), nameof(DayPercent), nameof(Eta),
            nameof(HealthLabel), nameof(HealthNote), nameof(HealthBrush), nameof(HealthBorderBrush),
            nameof(HasAlert), nameof(AlertText), nameof(AlertBrush),
            nameof(FooterText), nameof(FooterBrush), nameof(Countdown)
        })
        {
            Raise(name);
        }
    }

    static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
