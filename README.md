# Splicia

Trim, splice, rotate, crop and export videos on Windows, with frame-exact cuts. By [Vesperis Labs](https://www.vesperislabs.com).

## Download

Get the installer from the [latest release](https://github.com/fender90/Splicia/releases/latest) and run it. It installs for you alone or for everyone on the PC, adds Splicia to "Open with" for video files without changing your default apps, and uninstalls from Settings > Apps.

Requires Windows 10 or 11 (64-bit). See [CHANGELOG.md](CHANGELOG.md) for what's new.

## Features

- Opens the common video formats, including iPhone .mov (HEVC, HDR), .mp4, .mkv, .webm and .avi
- Trim from the start or end, frame by frame, or drag the selection along the timeline
- Splice: cut parts out and join the ends with a straight cut, a crossfade or a fade
- Rotate in 90° steps
- Crop with a common aspect ratio, the original one, or freely
- Export to H.264 or HEVC (hardware encoding when available, HDR kept), or keep the original streams
- Back-and-forth loop export

## Building

Needs the .NET 8 SDK. From PowerShell:

```powershell
.\build.ps1
```

This downloads FFmpeg 8.1 into `ffmpeg\` the first time, then writes the app to `publish\Splicia.exe` and the installer to `dist\Splicia-Setup-<version>.exe`. The version is set in [Directory.Build.props](Directory.Build.props).

## License

Splicia is © 2026 Vesperis Labs and licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The Splicia and Vesperis Labs names and logos are not covered by that license; see [LICENSE.txt](LICENSE.txt).

Splicia uses FFmpeg, which is licensed under the GNU GPL version 3. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for it and the other components, with links to their source code.
