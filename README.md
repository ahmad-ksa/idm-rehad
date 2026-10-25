# Rehad Download Manager

Windows download manager modelled on the classic download-accelerator workflow:

- **Engine** (`src/Rehad.Core`): up to 32 connections per file with dynamic segmentation, part files + assembly, resume after pause/crash, retries, speed limiter, download limits, proxy/SOCKS, site logins, HLS (AES-128) downloader, yt-dlp for video sites, site grabber, batch links.
- **App** (`src/Rehad`, WPF): categories, queues & scheduler, download progress window with connection map, Download File Info / complete dialogs, options, English + Arabic.
- **Browser helper** (`src/RehadHost`): native messaging host → named pipe `RehadPipe`.
- **Extension** (`extension/`): Chrome/Edge (MV3) and Firefox; takes over downloads, detects media, shows a “Download this video” button.

Build: GitHub Actions (`.github/workflows/build.yml`) produces `Rehad-Setup-x.y.z.exe` and extension packages.
Engine tests (Linux/Windows): `dotnet run --project tests/Rehad.Tests -- <baseUrl> <wwwDir>`.
