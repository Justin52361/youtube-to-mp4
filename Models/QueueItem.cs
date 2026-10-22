namespace YoutubeToMp4.Models;

public enum ItemStatus { Queued, Downloading, Merging, Done, Failed, Paused }

public sealed class QueueItem : ObservableObject
{
    ItemStatus _status = ItemStatus.Queued;
    double _percent;
    string? _error;

    public QueueItem(string videoId, string title, int? durationSeconds)
    {
        VideoId = videoId;
        Title = title;
        Url = "https://www.youtube.com/watch?v=" + videoId;
        ThumbUrl = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg";
        DurationText = FormatDuration(durationSeconds);
    }

    public string VideoId { get; }
    public string Title { get; }
    public string Url { get; }
    public string ThumbUrl { get; }
    public string DurationText { get; }

    public ItemStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) Raise(nameof(StatusText)); }
    }

    public double Percent
    {
        get => _percent;
        set { if (Set(ref _percent, value)) Raise(nameof(StatusText)); }
    }

    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) Raise(nameof(StatusText)); }
    }

    public string StatusText => Status switch
    {
        ItemStatus.Queued => "Queued",
        ItemStatus.Downloading => $"Downloading {Percent:0}%",
        ItemStatus.Merging => "Merging audio and video",
        ItemStatus.Done => "Done",
        ItemStatus.Paused => $"Paused at {Percent:0}%",
        ItemStatus.Failed => "Failed",
        _ => ""
    };

    static string FormatDuration(int? seconds)
    {
        if (seconds is null or <= 0) return "--:--";
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
