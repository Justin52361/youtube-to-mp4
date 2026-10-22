# YouTube to MP4

A Windows desktop app (WPF, .NET 10) for downloading YouTube videos and playlists as MP4 files, built on [yt-dlp](https://github.com/yt-dlp/yt-dlp) and [ffmpeg](https://ffmpeg.org/).

## Download

Grab `YoutubeToMp4.exe` from the [latest release](../../releases/latest). It is small but needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows x64); if it's missing, Windows shows a prompt with the download link. On first launch the app downloads yt-dlp and ffmpeg automatically if they are missing.

## Features

- Paste a video or playlist link and add it to a queue
- MP4 output with a selectable maximum quality (default 1080p)
- Choose where files are saved (default: `Videos\YouTube`)
- Pause, cancel all, and retry failed downloads
- Rate-limit protection to reduce the chance of YouTube bot checks:
  - **Download threads** presets (1, 2 or 4 videos at once), each with random waits between videos and hourly/daily caps that persist across restarts
  - Optional pause when YouTube blocks requests
  - Estimated finish time for the queue
- Optional use of your browser's login (cookies) for videos that need it

## Build from source

Requires the .NET 10 SDK on Windows.

```
dotnet run
```

To produce the framework-dependent executable (`bin\publish\YoutubeToMp4.exe`):

```
.\publish.ps1
```

## Disclaimer

Only download content you have the right to download. Respect YouTube's Terms of Service and the rights of content creators.
