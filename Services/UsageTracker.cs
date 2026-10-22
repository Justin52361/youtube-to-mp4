using System.Text.Json;
using YoutubeToMp4.Models;

namespace YoutubeToMp4.Services;

/// <summary>Remembers when each download started so the hourly and daily caps survive restarts.</summary>
public sealed class UsageTracker
{
    readonly object _lock = new();
    readonly List<DateTime> _starts = new();

    public UsageTracker()
    {
        try
        {
            if (File.Exists(AppPaths.UsageFile))
                _starts.AddRange(JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(AppPaths.UsageFile)) ?? new());
        }
        catch { /* start fresh */ }
        Prune();
    }

    public int UsedLastHour
    {
        get { lock (_lock) return WindowStarts().Count; }
    }

    public int UsedToday
    {
        get
        {
            var today = DateTime.Now.Date;
            lock (_lock) return _starts.Count(s => s.ToLocalTime().Date == today);
        }
    }

    public void Record()
    {
        lock (_lock)
        {
            _starts.Add(DateTime.UtcNow);
            Prune();
            Save();
        }
    }

    /// <summary>How long to hold off before another download may start, or zero if under both caps.</summary>
    public TimeSpan WaitFor(PacePreset preset, out string reason)
    {
        reason = "";
        var now = DateTime.UtcNow;
        var wait = TimeSpan.Zero;

        lock (_lock)
        {
            var window = WindowStarts();
            if (window.Count >= preset.PerHour)
            {
                var releasedBy = window[window.Count - preset.PerHour].AddHours(1) - now;
                if (releasedBy > wait) { wait = releasedBy; reason = "hourly limit"; }
            }

            var today = DateTime.Now.Date;
            if (_starts.Count(s => s.ToLocalTime().Date == today) >= preset.PerDay)
            {
                var untilMidnight = DateTime.Now.Date.AddDays(1) - DateTime.Now;
                if (untilMidnight > wait) { wait = untilMidnight; reason = "daily limit"; }
            }
        }
        return wait;
    }

    List<DateTime> WindowStarts()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        return _starts.Where(s => s > cutoff).OrderBy(s => s).ToList();
    }

    void Prune() => _starts.RemoveAll(s => s < DateTime.UtcNow.AddHours(-48));

    void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.UsageFile, JsonSerializer.Serialize(_starts));
        }
        catch { }
    }
}
