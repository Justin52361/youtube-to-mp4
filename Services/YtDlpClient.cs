using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YoutubeToMp4.Models;

namespace YoutubeToMp4.Services;

public enum OutcomeKind { Success, Cancelled, Blocked, Failed }

public sealed record DownloadOutcome(OutcomeKind Kind, string? Message = null);

public sealed record ResolvedVideo(string Id, string Title, int? DurationSeconds);

public sealed class ResolveException : Exception
{
    public ResolveException(string message, bool blocked) : base(message) => Blocked = blocked;
    public bool Blocked { get; }
}

/// <summary>Thin wrapper around the yt-dlp command line.</summary>
public sealed partial class YtDlpClient
{
    static readonly string[] BlockMarkers =
    {
        "not a bot", "sign in to confirm", "http error 429", "too many requests", "rate-limit", "rate limit"
    };

    readonly ToolManager _tools;
    readonly AppSettings _settings;

    public YtDlpClient(ToolManager tools, AppSettings settings)
    {
        _tools = tools;
        _settings = settings;
    }

    public static bool LooksBlocked(string text) =>
        BlockMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    public async Task<List<ResolvedVideo>> ResolveAsync(string url, CancellationToken ct)
    {
        var args = CommonArgs();
        args.AddRange(new[] { "--flat-playlist", "-J", "--no-warnings", url });

        var (exit, stdout, stderr) = await RunToEndAsync(args, ct);
        if (exit != 0)
        {
            var text = stderr + stdout;
            throw new ResolveException(LastError(stderr) ?? "yt-dlp could not read that link.", LooksBlocked(text));
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var videos = new List<ResolvedVideo>();

        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entries.EnumerateArray())
            {
                var video = ReadVideo(e);
                if (video != null) videos.Add(video);
            }
        }
        else
        {
            var video = ReadVideo(root);
            if (video != null) videos.Add(video);
        }
        return videos;
    }

    static ResolvedVideo? ReadVideo(JsonElement e)
    {
        var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;

        var title = e.TryGetProperty("title", out var t) ? t.GetString() ?? id : id;
        if (title.StartsWith("[Private video]") || title.StartsWith("[Deleted video]")) return null;

        int? duration = null;
        if (e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
            duration = (int)d.GetDouble();

        return new ResolvedVideo(id, title, duration);
    }

    public async Task<DownloadOutcome> DownloadAsync(QueueItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(_settings.OutputDir);
        var h = _settings.MaxHeight;

        var args = CommonArgs();
        args.AddRange(new[]
        {
            "-f", $"bv*[height<={h}][vcodec^=avc1]+ba[acodec^=mp4a]/b[height<={h}][ext=mp4]/bv*[height<={h}]+ba/b[height<={h}]",
            "--merge-output-format", "mp4",
            "--no-playlist",
            "--newline",
            "--windows-filenames",
            "--retries", "3",
            "--fragment-retries", "3",
            "--progress-template", "download:YTMP4 %(progress._percent_str)s",
            "--ffmpeg-location", _tools.FfmpegDir!,
            "-o", Path.Combine(_settings.OutputDir, "%(title)s [%(id)s].%(ext)s"),
            item.Url
        });

        var log = new StringBuilder();
        var errors = new List<string>();
        int streams = 0;

        using var process = new Process { StartInfo = NewStartInfo(args) };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (log) log.AppendLine(e.Data);

            var m = ProgressLine().Match(e.Data);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                // Video and audio arrive as separate streams: video fills the first 85%, audio the rest.
                item.Percent = streams <= 1 ? pct * 0.85 : 85 + pct * 0.15;
                if (item.Status != ItemStatus.Downloading) item.Status = ItemStatus.Downloading;
            }
            else if (e.Data.Contains("[download] Destination:")) streams++;
            else if (e.Data.StartsWith("[Merger]") || e.Data.StartsWith("[VideoRemuxer]"))
            {
                item.Percent = 95;
                item.Status = ItemStatus.Merging;
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (log) log.AppendLine(e.Data);
            if (e.Data.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) lock (errors) errors.Add(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var killOnCancel = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        await process.WaitForExitAsync(CancellationToken.None);

        if (ct.IsCancellationRequested) return new DownloadOutcome(OutcomeKind.Cancelled);
        if (process.ExitCode == 0) return new DownloadOutcome(OutcomeKind.Success);

        string all;
        lock (log) all = log.ToString();
        var message = errors.Count > 0 ? Clean(errors[^1]) : "yt-dlp exited with code " + process.ExitCode;
        return new DownloadOutcome(LooksBlocked(all) ? OutcomeKind.Blocked : OutcomeKind.Failed, message);
    }

    List<string> CommonArgs()
    {
        var args = new List<string> { "--ignore-config", "--no-color" };
        if (_settings.UseBrowserCookies)
        {
            args.Add("--cookies-from-browser");
            args.Add(_settings.CookieBrowser);
        }
        return args;
    }

    ProcessStartInfo NewStartInfo(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(_tools.YtDlpPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    async Task<(int Exit, string Stdout, string Stderr)> RunToEndAsync(IEnumerable<string> args, CancellationToken ct)
    {
        using var process = new Process { StartInfo = NewStartInfo(args) };
        process.Start();
        using var killOnCancel = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return (process.ExitCode, await stdout, await stderr);
    }

    static string? LastError(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));
        return line == null ? null : Clean(line);
    }

    static string Clean(string line) => Regex.Replace(line, @"^ERROR:\s*(\[[^\]]+\]\s*)?", "").Trim();

    [GeneratedRegex(@"YTMP4\s+([\d.]+)%")]
    private static partial Regex ProgressLine();
}
