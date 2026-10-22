using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;

namespace YoutubeToMp4.Services;

/// <summary>Finds yt-dlp and ffmpeg, and downloads them into the app's folder when they are missing.</summary>
public sealed class ToolManager
{
    const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    static readonly HttpClient Http = CreateClient();

    public string? YtDlpPath { get; private set; }
    public string? FfmpegDir { get; private set; }
    public bool Ready => YtDlpPath != null && FfmpegDir != null;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("YoutubeToMp4/1.0");
        return c;
    }

    public async Task EnsureAsync(IProgress<string> report, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.ToolsDir);

        YtDlpPath = Locate("yt-dlp.exe");
        if (YtDlpPath == null)
        {
            var target = Path.Combine(AppPaths.ToolsDir, "yt-dlp.exe");
            await DownloadFileAsync(YtDlpUrl, target, "yt-dlp", report, ct);
            YtDlpPath = target;
            TouchUpdateMarker();
        }
        else if (IsOurs(YtDlpPath))
        {
            await TryUpdateYtDlpAsync(report, ct);
        }

        var ffmpeg = Locate("ffmpeg.exe");
        if (ffmpeg == null)
        {
            var zip = Path.Combine(AppPaths.ToolsDir, "ffmpeg.zip");
            await DownloadFileAsync(FfmpegUrl, zip, "ffmpeg", report, ct);
            report.Report("Unpacking ffmpeg...");
            await Task.Run(() => ExtractFfmpeg(zip), ct);
            File.Delete(zip);
            ffmpeg = Path.Combine(AppPaths.ToolsDir, "ffmpeg.exe");
        }
        FfmpegDir = Path.GetDirectoryName(ffmpeg);
    }

    static string? Locate(string exe)
    {
        var local = Path.Combine(AppPaths.ToolsDir, exe);
        if (File.Exists(local)) return local;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    static bool IsOurs(string path) =>
        string.Equals(Path.GetDirectoryName(path), AppPaths.ToolsDir, StringComparison.OrdinalIgnoreCase);

    static string MarkerFile => Path.Combine(AppPaths.ToolsDir, "yt-dlp.updated");

    static void TouchUpdateMarker() => File.WriteAllText(MarkerFile, DateTime.UtcNow.ToString("O"));

    /// <summary>YouTube changes often, so keep our copy of yt-dlp current (at most once a day).</summary>
    async Task TryUpdateYtDlpAsync(IProgress<string> report, CancellationToken ct)
    {
        if (File.Exists(MarkerFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(MarkerFile) < TimeSpan.FromDays(1))
            return;

        report.Report("Checking for a yt-dlp update...");
        try
        {
            var psi = new ProcessStartInfo(YtDlpPath!, "-U")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await p.WaitForExitAsync(timeout.Token);
            TouchUpdateMarker();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception) when (!ct.IsCancellationRequested) { /* offline: keep the version we have */ }
    }

    static void ExtractFfmpeg(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var name = entry.Name;
            if (!name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)) continue;
            entry.ExtractToFile(Path.Combine(AppPaths.ToolsDir, name), overwrite: true);
        }
    }

    static async Task DownloadFileAsync(string url, string target, string label, IProgress<string> report, CancellationToken ct)
    {
        report.Report($"Downloading {label}...");
        var temp = target + ".tmp";
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var dest = File.Create(temp);

            var buffer = new byte[81920];
            long read = 0;
            int lastPct = -1;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0)
                {
                    var pct = (int)(read * 100 / total.Value);
                    if (pct != lastPct) { lastPct = pct; report.Report($"Downloading {label} {pct}%"); }
                }
            }
        }
        File.Move(temp, target, overwrite: true);
    }
}
