namespace YoutubeToMp4.Models;

/// <summary>One "Download threads" choice: how many videos run at once and the limits that go with it.</summary>
public sealed record PacePreset(
    string Key,
    string Name,
    int Threads,
    int MinDelaySec,
    int MaxDelaySec,
    int PerHour,
    int PerDay,
    string Blurb)
{
    public string DelayText => $"{MinDelaySec} to {MaxDelaySec} sec";
    public double AverageDelaySec => (MinDelaySec + MaxDelaySec) / 2.0;

    public static readonly IReadOnlyList<PacePreset> All = new[]
    {
        new PacePreset("1", "1 thread", 1, 25, 60, 20, 120, "Safest. One video at a time, like a person browsing."),
        new PacePreset("2", "2 threads", 2, 12, 30, 40, 250, "Good for most playlists. Two videos at once with short random waits."),
        new PacePreset("4", "4 threads", 4, 4, 10, 90, 500, "Quick, but more likely to trigger a bot check. Use sparingly.")
    };

    public static PacePreset Get(string key) => All.FirstOrDefault(p => p.Key == key) ?? All[1];
}
