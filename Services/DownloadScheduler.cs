using System.Diagnostics;
using YoutubeToMp4.Models;

namespace YoutubeToMp4.Services;

/// <summary>
/// Decides when the next download may start: random gaps between starts, a cap on parallel
/// downloads, hourly and daily limits, and an automatic pause when YouTube pushes back.
/// </summary>
public sealed class DownloadScheduler : IDisposable
{
    readonly AppSettings _settings;
    readonly UsageTracker _usage;
    readonly YtDlpClient _yt;
    readonly Random _rng = new();
    readonly object _lock = new();
    readonly LinkedList<QueueItem> _pending = new();
    readonly Dictionary<QueueItem, CancellationTokenSource> _active = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly List<double> _downloadSeconds = new();
    readonly HashSet<QueueItem> _cancelled = new();

    DateTime _nextStartUtc = DateTime.UtcNow;
    bool _paused;
    bool _started;

    public DownloadScheduler(AppSettings settings, UsageTracker usage, YtDlpClient yt)
    {
        _settings = settings;
        _usage = usage;
        _yt = yt;
    }

    /// <summary>Raised from background threads whenever state the UI shows may have changed.</summary>
    public event Action? Changed;

    public bool ToolsReady { get; set; }
    public bool IsPaused => _paused;

    /// <summary>Set when the pause came from a block or rate limit rather than the user.</summary>
    public string? BlockReason { get; private set; }

    /// <summary>Why the next download is being held back (for example the hourly cap), if it is.</summary>
    public string? HoldNotice { get; private set; }

    public bool HadRecentBlock { get; private set; }

    public DateTime NextStartUtc => _nextStartUtc;
    public int ActiveCount { get { lock (_lock) return _active.Count; } }
    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    public double AverageDownloadSeconds
    {
        get { lock (_downloadSeconds) return _downloadSeconds.Count == 0 ? 40 : _downloadSeconds.Average(); }
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(() => LoopAsync(_shutdown.Token));
    }

    public void Enqueue(QueueItem item)
    {
        item.Status = ItemStatus.Queued;
        lock (_lock) _pending.AddLast(item);
        Changed?.Invoke();
    }

    public void Requeue(IEnumerable<QueueItem> items)
    {
        foreach (var item in items)
        {
            item.Error = null;
            item.Percent = 0;
            Enqueue(item);
        }
    }

    /// <summary>Stop starting downloads and halt the running ones (yt-dlp resumes partial files later).</summary>
    public void Pause(string? blockReason = null)
    {
        _paused = true;
        if (blockReason != null)
        {
            BlockReason = blockReason;
            HadRecentBlock = true;
        }

        lock (_lock)
        {
            foreach (var cts in _active.Values) cts.Cancel();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Drop everything that has not finished: queued items are forgotten and running downloads are
    /// stopped without being requeued. Returns the items that were dropped.
    /// </summary>
    public List<QueueItem> CancelAll()
    {
        List<QueueItem> dropped;
        lock (_lock)
        {
            dropped = _pending.ToList();
            _pending.Clear();
            foreach (var (item, cts) in _active)
            {
                _cancelled.Add(item);
                dropped.Add(item);
                cts.Cancel();
            }
        }
        HoldNotice = null;
        if (_paused) Resume();
        else Changed?.Invoke();
        return dropped;
    }

    public void Resume()
    {
        // After a block, wait a good while before touching YouTube again.
        if (BlockReason != null) _nextStartUtc = DateTime.UtcNow.AddSeconds(90);
        BlockReason = null;
        _paused = false;
        Changed?.Invoke();
    }

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var preset = PacePreset.Get(_settings.PaceKey);

                if (!ToolsReady || _paused || ActiveCount >= preset.Threads || DateTime.UtcNow < _nextStartUtc)
                {
                    await Task.Delay(250, ct);
                    continue;
                }

                QueueItem? item;
                lock (_lock)
                {
                    item = _pending.First?.Value;
                    if (item != null) _pending.RemoveFirst();
                }
                if (item == null)
                {
                    HoldNotice = null;
                    await Task.Delay(400, ct);
                    continue;
                }

                var wait = _usage.WaitFor(preset, out var reason);
                if (wait > TimeSpan.Zero)
                {
                    lock (_lock) _pending.AddFirst(item);
                    HoldNotice = $"Waiting for the {reason}, resumes at {DateTime.Now + wait:t}";
                    Changed?.Invoke();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, 5)), ct);
                    continue;
                }

                HoldNotice = null;
                _usage.Record();
                _nextStartUtc = DateTime.UtcNow + NextGap(preset);

                var cts = new CancellationTokenSource();
                lock (_lock) _active[item] = cts;
                item.Error = null;
                item.Status = ItemStatus.Downloading;
                _ = Task.Run(() => RunItemAsync(item, cts));
                Changed?.Invoke();
            }
            catch (OperationCanceledException) { break; }
        }
    }

    TimeSpan NextGap(PacePreset preset)
    {
        var seconds = _settings.RandomizeWaits
            ? preset.MinDelaySec + _rng.NextDouble() * (preset.MaxDelaySec - preset.MinDelaySec)
            : preset.AverageDelaySec;
        return TimeSpan.FromSeconds(seconds);
    }

    async Task RunItemAsync(QueueItem item, CancellationTokenSource cts)
    {
        var timer = Stopwatch.StartNew();
        DownloadOutcome outcome;
        try
        {
            outcome = await _yt.DownloadAsync(item, cts.Token);
        }
        catch (Exception ex)
        {
            outcome = new DownloadOutcome(OutcomeKind.Failed, ex.Message);
        }

        bool wasCancelled;
        lock (_lock)
        {
            _active.Remove(item);
            wasCancelled = _cancelled.Remove(item);
        }
        cts.Dispose();

        if (wasCancelled && outcome.Kind != OutcomeKind.Success)
        {
            item.Status = ItemStatus.Failed;
            item.Error = "Cancelled";
            Changed?.Invoke();
            return;
        }

        switch (outcome.Kind)
        {
            case OutcomeKind.Success:
                item.Percent = 100;
                item.Status = ItemStatus.Done;
                lock (_downloadSeconds) _downloadSeconds.Add(timer.Elapsed.TotalSeconds);
                break;

            case OutcomeKind.Cancelled:
                item.Status = ItemStatus.Paused;
                lock (_lock) _pending.AddFirst(item);
                break;

            case OutcomeKind.Blocked when _settings.PauseWhenBlocked:
                item.Status = ItemStatus.Queued;
                item.Percent = 0;
                lock (_lock) _pending.AddFirst(item);
                Pause("YouTube is asking for verification or limiting requests. Paused to protect your connection.");
                break;

            default:
                item.Error = outcome.Message;
                item.Status = ItemStatus.Failed;
                break;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        lock (_lock)
        {
            foreach (var cts in _active.Values) cts.Cancel();
        }
    }
}
