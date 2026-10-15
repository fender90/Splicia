using FFmpeg.AutoGen;

namespace VideoTrimmer;

/// <summary>
/// Locates and loads the bundled FFmpeg build (the ffmpeg\ folder next to the exe: libav* DLLs used in-process for
/// decoding, plus ffmpeg.exe which does the exporting).
/// </summary>
static class FFmpegRuntime
{
    static bool _loaded;

    public static string AppDirectory => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    public static string Directory => Path.Combine(AppDirectory, "ffmpeg");
    public static string FFmpegExe => Path.Combine(Directory, "ffmpeg.exe");

    /// <summary>The files the app needs (the installer's payload must contain all of them).</summary>
    public static readonly string[] RequiredFiles =
    {
        "avcodec-62.dll", "avformat-62.dll", "avutil-60.dll", "swscale-9.dll", "swresample-6.dll",
        "avfilter-11.dll", "avdevice-62.dll", "ffmpeg.exe",
    };

    public static bool IsPresent(string dir) => RequiredFiles.All(f => File.Exists(Path.Combine(dir, f)));

    /// <summary>Loads the libraries once. Returns an error message when they are missing or can't be loaded.</summary>
    public static string? Load()
    {
        if (_loaded) return null;
        if (!IsPresent(Directory))
            return $"The FFmpeg components are missing from\n{Directory}\n\nReinstall Video Trimmer to restore them.";
        try
        {
            ffmpeg.RootPath = Directory;
            DynamicallyLoadedBindings.Initialize();
            // Touch every library so a broken install fails here rather than halfway through opening a file.
            _ = ffmpeg.avutil_version() + ffmpeg.avcodec_version() + ffmpeg.avformat_version() +
                ffmpeg.swscale_version() + ffmpeg.swresample_version();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            _loaded = true;
            return null;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Loading FFmpeg");
            return $"The FFmpeg components in\n{Directory}\ncouldn't be loaded:\n\n{ex.Message}";
        }
    }
}
