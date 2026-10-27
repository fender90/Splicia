using System.Diagnostics;
using System.Globalization;

namespace Splicia;

/// <summary>Locations and helpers shared by the harness tests.</summary>
static class H
{
    // bin\Release\net8.0-windows\ → the harness folder
    static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\.."));
    public static readonly string Vids = Path.Combine(Root, "vids");
    public static readonly string Out = Path.Combine(Root, "out");
    public static int Fails;

    public static void Check(bool ok, string what)
    {
        if (!ok) Fails++;
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
    }

    public static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (o + err.Result).Trim();
    }

    public static string Ffmpeg(string args) => Run(FFmpegRuntime.FFmpegExe, args);
    public static string Probe(string file, string args) => Run(Path.Combine(FFmpegRuntime.Directory, "ffprobe.exe"), $"-v error {args} \"{file}\"");
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        if (FFmpegRuntime.Load() is string err) { Console.WriteLine(err); return 1; }
        Directory.CreateDirectory(H.Vids);
        Directory.CreateDirectory(H.Out);
        foreach (var mode in args.Length == 0 ? new[] { "vids", "crop", "ui" } : args)
        {
            Console.WriteLine($"=== {mode}");
            switch (mode)
            {
                case "vids": MakeVideos(); break;
                case "crop": CropTests.Run(); break;
                case "ui": CropUiTests.Run(); break;
                case "brand": BrandUiTests.Run(); break;
                case "timeline": TimelineTests.Run(); break;
                case "timelineui": TimelineUiTests.Run(); break;
                case "play": PlaybackTests.Run(); break;
                default: Console.WriteLine($"unknown mode {mode}"); return 1;
            }
        }
        Console.WriteLine(H.Fails == 0 ? "ALL PASSED" : $"{H.Fails} FAILURES");
        return H.Fails == 0 ? 0 : 2;
    }

    /// <summary>Test clips (made once): a numbered-frame clip, 1080p testsrc, iPhone-like 4K HEVC HLG portrait, 60 fps MKV, 29.97 fps MP4, two sound files.</summary>
    static void MakeVideos()
    {
        void Make(string name, string args)
        {
            string path = Path.Combine(H.Vids, name);
            if (File.Exists(path)) { Console.WriteLine($"  {name} exists"); return; }
            string r = H.Ffmpeg($"-hide_banner -loglevel error -y {args} \"{path}\"");
            Console.WriteLine($"  {name} {(File.Exists(path) ? "made" : "FAILED " + r)}");
        }
        Make("t7_counter.mp4", "-f lavfi -i \"color=c=0x203050:s=1280x720:r=30:d=10,hue=h=t*36,drawtext=fontfile='C\\:/Windows/Fonts/arialbd.ttf':text='%{n}':fontsize=360:fontcolor=white:x=(w-tw)/2:y=(h-th)/2\" " +
                               "-f lavfi -i \"sine=f=440:beep_factor=4:duration=10\" -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -g 45 -c:a aac");
        Make("t1_h264_1080p30.mp4", "-f lavfi -i \"testsrc=size=1920x1080:rate=30:duration=20\" -f lavfi -i \"sine=f=440:beep_factor=4:duration=20\" " +
                                    "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -g 60 -c:a aac -b:a 128k");
        string tmp = Path.Combine(H.Vids, "tmp_hlg.mov");
        if (!File.Exists(Path.Combine(H.Vids, "t2_iphone_like.mov")))
        {
            H.Ffmpeg($"-hide_banner -loglevel error -y -f lavfi -i \"testsrc2=size=3840x2160:rate=30:duration=8\" -f lavfi -i \"sine=f=660:beep_factor=2:duration=8\" " +
                     "-vf \"setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc:range=tv,format=yuv420p10le\" " +
                     $"-c:v libx265 -preset ultrafast -x265-params \"log-level=error:keyint=30\" -tag:v hvc1 -c:a aac -b:a 128k \"{tmp}\"");
            Make("t2_iphone_like.mov", $"-display_rotation:v:0 -90 -i \"{tmp}\" -c copy");
            File.Delete(tmp);
        }
        Make("t5_noaudio.mkv", "-f lavfi -i \"testsrc=size=1280x720:rate=60:duration=6\" -c:v libx264 -preset veryfast");
        Make("t8_ntsc.mp4", "-f lavfi -i \"testsrc=size=640x360:rate=30000/1001:duration=8\" -f lavfi -i \"sine=f=500:duration=8\" " +
                            "-c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -shortest");
        // Sound files for the audio track: a 1 kHz tone (WAV) and a 300 Hz tone (MP3).
        Make("s1_tone.wav", "-f lavfi -i \"sine=f=1000:duration=3:sample_rate=44100\" -ac 2 -c:a pcm_s16le");
        Make("s2_low.mp3", "-f lavfi -i \"sine=f=300:duration=4:sample_rate=44100\" -ac 2 -c:a libmp3lame -b:a 192k");
    }
}
