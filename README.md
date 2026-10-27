# Splicia

Cut, arrange, rotate, crop and export videos on Windows, with frame-exact cuts and an audio track. By [Vesperis Labs](https://www.vesperislabs.com).

## Download

Get the installer from the [latest release](https://github.com/fender90/Splicia/releases/latest) and run it. It installs for you alone or for everyone on the PC, adds Splicia to "Open with" for video files without changing your default apps, and uninstalls from Settings > Apps.

Requires Windows 10 or 11 (64-bit). See [CHANGELOG.md](CHANGELOG.md) for what's new.

## Features

- Opens the common video formats, including iPhone .mov (HEVC, HDR), .mp4, .mkv, .webm and .avi
- A timeline of clips: split at the playhead, delete (leaving a gap or closing it), drag clips to move or rearrange them; undo and redo
- Transitions where clips meet: a straight cut, a crossfade or a fade through black
- Fade in and out on any clip, video or sound
- An audio track: drop WAV or MP3 files on it, move them, trim their ends
- Gaps play and export as black and silence; the timeline is as long as its last clip
- Playback plays the timeline exactly as it exports: no stall at cuts, transitions shown, sound mixed without clicks
- The control panel docked under the video, or floating over it and hiding when idle
- Rotate in 90° steps
- Crop with a common aspect ratio, the original one, or freely
- Export to H.264 or HEVC (hardware encoding when available, HDR kept), or keep the original streams
- Back-and-forth loop export (of a single clip)

## Building

Needs the .NET 8 SDK. From PowerShell:

```powershell
.\build.ps1
```

This downloads FFmpeg 8.1 into `ffmpeg\` the first time, then writes the app to `publish\Splicia.exe` and the installer to `dist\Splicia-Setup-<version>.exe`. The version is set in [Directory.Build.props](Directory.Build.props).

## License

Splicia is © 2026 Vesperis Labs and licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The Splicia and Vesperis Labs names and logos are not covered by that license; see [LICENSE.txt](LICENSE.txt).

Splicia uses FFmpeg, which is licensed under the GNU GPL version 3. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for it and the other components, with links to their source code.
