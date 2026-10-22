# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

WPF app on `net10.0-windows`, no NuGet packages, no tests, no linter configured.

```bash
dotnet build
dotnet run
```

The exe lands in `bin/Debug/net10.0-windows/YoutubeToMp4.exe`. Close the running app before rebuilding, or the build fails on the locked exe.

`System.IO` and `System.Net.Http` are added as global usings in the csproj (WPF doesn't import them implicitly).

## Distribution

`publish.ps1` publishes a framework-dependent single-file exe to `bin/publish`. It requires the .NET 10 Desktop Runtime on the user's PC (the apphost shows a download prompt if missing); there is deliberately no launcher/bootstrapper.

## Architecture

The app is a front end over the **yt-dlp.exe** and **ffmpeg** command lines. Nothing is downloaded in-process.

- `Services/ToolManager` fetches yt-dlp (GitHub latest release) and ffmpeg/ffprobe (yt-dlp/FFmpeg-Builds win64 zip) into `%AppData%\YoutubeToMp4\tools` on first run, and runs `yt-dlp -U` at most once a day. Settings (`settings.json`) and hourly/daily usage (`usage.json`) live in `%AppData%\YoutubeToMp4` too.
- `Services/YtDlpClient` resolves links (`--flat-playlist -J`) and runs one download per `QueueItem`, parsing progress from a custom `--progress-template` (`YTMP4 NN%`). Video fills 0–85% and audio 85–100%. It also classifies output as Success / Cancelled / Blocked / Failed by string-matching for bot checks and HTTP 429 (`LooksBlocked`).
- `Services/DownloadScheduler` is the core. A background loop starts the next queued item only when tools are ready, it isn't paused, active downloads are below the thread count, the random gap since the last start has passed, and `UsageTracker` allows it (hourly/daily caps). Outcomes drive state:
  - Blocked: requeue and auto-pause. Resume then waits 90 seconds.
  - Cancelled by Pause: requeue at the front, since yt-dlp resumes `.part` files.
  - Cancelled by `CancelAll`: the item is tracked in `_cancelled` so it is not requeued.
- `Models/PacePreset` defines the 1/2/4-thread presets (delay range, per-hour and per-day caps). This anti-rate-limit behavior is the point of the app, so keep changes to it deliberate.
- `ViewModels/MainViewModel` is the single view model. Scheduler events arrive on background threads and are marshalled to the UI with `Dispatcher.BeginInvoke(Refresh)`; a 500 ms timer also calls `Refresh()`, which raises PropertyChanged for all the computed display values (queue summary, block-risk label, countdown, and so on). A new computed property must be added to that list in `Refresh()` to update live.
- Views are chromeless (`WindowChrome`, custom title bar). Theme brushes and control styles (PillList, Switch, ThinBar, and others) are in `App.xaml`. `SettingsWindow` shares the main view model as its DataContext.

## Design source

The UI mirrors an approved mockup published as a Design artifact (`https://claude.ai/artifact/UXUaitKSLDVCQsbcZTK5YB`). The user's visual direction is a dark utilitarian look and the title "YouTube → MP4" with no subtitle. The app icon is a YouTube play button wearing a pirate hat: `Assets/AppIcon.xaml` is the vector source (also used in the title bar) and `Assets/AppIcon.ico` is rendered from it by `tools/make-icon.ps1`.
