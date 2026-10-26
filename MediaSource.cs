using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using static FFmpeg.AutoGen.ffmpeg;

namespace Splicia;

/// <summary>What the app knows about an opened file. Times are seconds from the start of the file.</summary>
sealed record MediaInfo
{
    public required string Path { get; init; }
    public required string Container { get; init; }
    public long FileSize { get; init; }
    public double Duration { get; init; }

    // Video (Width/Height are the stored picture, before rotation; Sar = pixel aspect ratio)
    public int Width { get; init; }
    public int Height { get; init; }
    public double Sar { get; init; } = 1;
    /// <summary>Clockwise rotation the file asks players to apply (phones record portrait video this way).</summary>
    public int Rotation { get; init; }
    public double Fps { get; init; }
    public required string VideoCodec { get; init; }
    public int BitDepth { get; init; } = 8;
    public string ColorPrimaries { get; init; } = "";
    public string ColorTransfer { get; init; } = "";
    public string ColorSpace { get; init; } = "";
    public string ColorRange { get; init; } = "";
    public bool IsHlg => ColorTransfer == "arib-std-b67";
    public bool IsPq => ColorTransfer == "smpte2084";
    public bool IsHdr => IsHlg || IsPq;

    // Audio
    public bool HasAudio => AudioCodec != null;
    public string? AudioCodec { get; init; }
    public int AudioChannels { get; init; }
    public int AudioRate { get; init; }

    public double FrameDuration => Fps > 0 ? 1 / Fps : 1 / 30.0;

    /// <summary>Picture size as displayed: pixel aspect applied, then the file's own rotation.</summary>
    public (int W, int H) DisplaySize(int extraRotation = 0)
    {
        int w = (int)Math.Round(Width * Sar), h = Height;
        return ((Rotation + extraRotation) % 180 == 0) ? (w, h) : (h, w);
    }
}

/// <summary>A decoded picture as BGRA (not rotated). Pooled and reused by the playback engine.</summary>
sealed class VideoFrame
{
    public byte[] Data = Array.Empty<byte>();
    public int Width, Height;
    public int Stride => Width * 4;
    public double Time;
    /// <summary>False for the approximate (nearest keyframe) previews shown while scrubbing.</summary>
    public bool Exact = true;

    public void EnsureSize(int w, int h)
    {
        Width = w;
        Height = h;
        if (Data.Length < w * h * 4) Data = new byte[w * h * 4];
    }
}

/// <summary>
/// Demuxes and decodes one file with FFmpeg's libraries: video to BGRA at a chosen size, audio to 48 kHz stereo 16-bit.
/// Not thread-safe; each user (playback engine, thumbnail strip) opens its own instance.
/// </summary>
unsafe sealed class MediaSource : IDisposable
{
    public const int AudioRate = 48000;

    public enum Got { Video, Audio, End }

    public readonly MediaInfo Info;

    AVFormatContext* _fmt;
    AVCodecContext* _vctx, _actx;
    AVPacket* _pkt;
    AVFrame* _vframe, _aframe, _cur, _pushback;
    double _curTime, _pushbackTime;
    SwsContext* _sws;
    (int W, int H, int Fmt, int Cs, int Range, int OutW, int OutH) _swsKey;
    SwrContext* _swr;
    (int Fmt, int Rate, ulong Layout, int Channels) _swrKey;
    readonly int _vi = -1, _ai = -1;
    readonly AVRational _vtb, _atb;
    readonly double _start;
    bool _inputEnded, _vDone, _aDone, _audioEnabled;
    double _lastVideoTime = double.NaN;
    byte[] _pcm = Array.Empty<byte>();

    /// <summary>Whether audio packets are decoded. Changing it takes effect after the next <see cref="Seek"/>.</summary>
    public bool AudioEnabled { get => _audioEnabled; set => _audioEnabled = value && _actx != null; }

    /// <param name="keyframesOnly">Decode only keyframes (fast seeking for thumbnails).</param>
    /// <param name="decoders">False opens the container only (for inspecting packets).</param>
    /// <param name="threads">Video decoder threads (0 = by resolution × frame rate). Frame threading keeps a picture in
    /// flight per thread, so more threads cost memory (≈25 MB each at 4K) and seek latency.</param>
    public MediaSource(string path, bool keyframesOnly = false, bool decoders = true, int threads = 0)
    {
        try
        {
            AVFormatContext* fmt = null;
            Check(avformat_open_input(&fmt, path, null, null), "Couldn't open the file");
            _fmt = fmt;
            Check(avformat_find_stream_info(_fmt, null), "Couldn't read the file's streams");

            AVCodec* vcodec = null, acodec = null;
            _vi = av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &vcodec, 0);
            // Cover art in audio files shows up as a single-picture "video" stream: not a video.
            if (_vi >= 0 && (_fmt->streams[_vi]->disposition & AV_DISPOSITION_ATTACHED_PIC) != 0) _vi = -1;
            if (_vi < 0) throw new NotSupportedException("This file has no video.");
            _ai = av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, _vi, &acodec, 0);

            var vst = _fmt->streams[_vi];
            _vtb = vst->time_base;
            _start = _fmt->start_time != AV_NOPTS_VALUE ? _fmt->start_time / (double)AV_TIME_BASE : 0;
            var par = vst->codecpar;

            if (decoders)
            {
                if (threads <= 0)
                {
                    // 3 threads for 1080p30, 6 for 4K30, 10 for 4K60
                    double rate = Q(vst->avg_frame_rate) is > 0 and < 1000 and var r ? r : 30;
                    threads = (int)Math.Round(2 + (double)par->width * par->height * rate / (1920 * 1080 * 30.0));
                }
                _vctx = OpenDecoder(vst, vcodec, video: true, Math.Clamp(threads, 2, Math.Min(12, Environment.ProcessorCount)));
                if (keyframesOnly) _vctx->skip_frame = AVDiscard.AVDISCARD_NONKEY;
                if (_ai >= 0)
                {
                    try { _actx = OpenDecoder(_fmt->streams[_ai], acodec, video: false, 1); }
                    catch (Exception ex) { CrashLog.Write(ex, "Opening audio decoder (continuing without sound)"); _ai = -1; }
                }
            }
            _pkt = av_packet_alloc();
            _vframe = av_frame_alloc();
            _aframe = av_frame_alloc();
            _cur = av_frame_alloc();
            _pushback = av_frame_alloc();
            if (_ai >= 0) _atb = _fmt->streams[_ai]->time_base;

            var sar = av_guess_sample_aspect_ratio(_fmt, vst, null);
            double fps = Q(vst->avg_frame_rate);
            if (fps <= 0 || fps > 1000) fps = Q(vst->r_frame_rate);
            if (fps <= 0 || fps > 1000) fps = 30;
            double duration = _fmt->duration != AV_NOPTS_VALUE ? _fmt->duration / (double)AV_TIME_BASE
                : vst->duration != AV_NOPTS_VALUE ? vst->duration * Q(_vtb) : 0;

            var desc = av_pix_fmt_desc_get((AVPixelFormat)par->format);
            int depth = desc != null ? desc->comp[0].depth : 8;
            AVCodecParameters* apar = _ai >= 0 ? _fmt->streams[_ai]->codecpar : null;

            Info = new MediaInfo
            {
                Path = path,
                Container = Str(_fmt->iformat->long_name) ?? Str(_fmt->iformat->name) ?? "",
                FileSize = new FileInfo(path).Length,
                Duration = Math.Max(duration, 0),
                Width = par->width,
                Height = par->height,
                Sar = sar.num > 0 && sar.den > 0 ? Q(sar) : 1,
                Rotation = ReadRotation(par),
                Fps = fps,
                VideoCodec = CodecName(par->codec_id, par->profile),
                BitDepth = depth,
                ColorPrimaries = av_color_primaries_name(par->color_primaries) ?? "",
                ColorTransfer = av_color_transfer_name(par->color_trc) ?? "",
                ColorSpace = av_color_space_name(par->color_space) ?? "",
                ColorRange = av_color_range_name(par->color_range) ?? "",
                AudioCodec = apar != null ? CodecName(apar->codec_id, apar->profile) : null,
                AudioChannels = apar != null ? apar->ch_layout.nb_channels : 0,
                AudioRate = apar != null ? apar->sample_rate : 0,
            };
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    static AVCodecContext* OpenDecoder(AVStream* st, AVCodec* codec, bool video, int threads)
    {
        if (codec == null) codec = avcodec_find_decoder(st->codecpar->codec_id);
        if (codec == null)
            throw new NotSupportedException($"There's no decoder for {avcodec_get_name(st->codecpar->codec_id)} {(video ? "video" : "audio")}.");
        var ctx = avcodec_alloc_context3(codec);
        try
        {
            Check(avcodec_parameters_to_context(ctx, st->codecpar), "Couldn't set up the decoder");
            ctx->pkt_timebase = st->time_base;
            if (video)
            {
                ctx->thread_count = threads;
                ctx->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
            }
            Check(avcodec_open2(ctx, codec, null), "Couldn't open the decoder");
            return ctx;
        }
        catch
        {
            avcodec_free_context(&ctx);
            throw;
        }
    }

    static int ReadRotation(AVCodecParameters* par)
    {
        var sd = av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (sd == null || sd->size < 36) return 0;
        var m = *(int_array9*)sd->data;
        double ccw = av_display_rotation_get(in m);
        if (double.IsNaN(ccw)) return 0;
        int cw = (int)Math.Round(-ccw / 90.0) * 90; // the matrix holds counter-clockwise degrees
        return ((cw % 360) + 360) % 360;
    }

    static string CodecName(AVCodecID id, int profile) => id switch
    {
        AVCodecID.AV_CODEC_ID_H264 => "H.264",
        AVCodecID.AV_CODEC_ID_HEVC => "HEVC",
        AVCodecID.AV_CODEC_ID_VP8 => "VP8",
        AVCodecID.AV_CODEC_ID_VP9 => "VP9",
        AVCodecID.AV_CODEC_ID_AV1 => "AV1",
        AVCodecID.AV_CODEC_ID_MPEG4 => "MPEG-4",
        AVCodecID.AV_CODEC_ID_MPEG2VIDEO => "MPEG-2",
        AVCodecID.AV_CODEC_ID_MPEG1VIDEO => "MPEG-1",
        AVCodecID.AV_CODEC_ID_PRORES => "ProRes",
        AVCodecID.AV_CODEC_ID_MJPEG => "Motion JPEG",
        AVCodecID.AV_CODEC_ID_DVVIDEO => "DV",
        AVCodecID.AV_CODEC_ID_WMV3 => "WMV 9",
        AVCodecID.AV_CODEC_ID_VC1 => "VC-1",
        AVCodecID.AV_CODEC_ID_AAC => "AAC",
        AVCodecID.AV_CODEC_ID_MP3 => "MP3",
        AVCodecID.AV_CODEC_ID_OPUS => "Opus",
        AVCodecID.AV_CODEC_ID_VORBIS => "Vorbis",
        AVCodecID.AV_CODEC_ID_AC3 => "Dolby Digital",
        AVCodecID.AV_CODEC_ID_EAC3 => "Dolby Digital Plus",
        AVCodecID.AV_CODEC_ID_FLAC => "FLAC",
        AVCodecID.AV_CODEC_ID_ALAC => "ALAC",
        AVCodecID.AV_CODEC_ID_WMAV2 => "WMA",
        _ when id >= AVCodecID.AV_CODEC_ID_PCM_S16LE && id < AVCodecID.AV_CODEC_ID_ADPCM_IMA_QT => "PCM",
        _ => (avcodec_get_name(id) ?? "unknown").ToUpperInvariant(),
    };

    // ───────────────────────── decoding ─────────────────────────

    /// <summary>
    /// Moves to the keyframe at or before <paramref name="time"/> and resets the decoders. Frames then come out from
    /// that keyframe on; callers skip forward to the time they want.
    /// </summary>
    public void Seek(double time)
    {
        long ts = (long)((Math.Max(0, time) + _start) * AV_TIME_BASE);
        if (avformat_seek_file(_fmt, -1, long.MinValue, ts, ts, 0) < 0)
            av_seek_frame(_fmt, -1, ts, AVSEEK_FLAG_BACKWARD);
        if (_vctx != null) avcodec_flush_buffers(_vctx);
        if (_actx != null) avcodec_flush_buffers(_actx);
        av_frame_unref(_pushback);
        av_frame_unref(_cur);
        _inputEnded = _vDone = _aDone = false;
        _lastVideoTime = double.NaN;
    }

    /// <summary>Decodes until the next video or audio frame (see <see cref="VideoTime"/> / <see cref="AudioTime"/>).</summary>
    public Got Next()
    {
        if (_pushback->buf[0] != null)
        {
            av_frame_unref(_vframe);
            av_frame_move_ref(_vframe, _pushback);
            _lastVideoTime = _pushbackTime;
            return Got.Video;
        }
        while (true)
        {
            if (_vctx != null && !_vDone)
            {
                int r = avcodec_receive_frame(_vctx, _vframe);
                if (r == 0)
                {
                    double t = FrameTime(_vframe, _vtb);
                    if (double.IsNaN(t)) t = double.IsNaN(_lastVideoTime) ? 0 : _lastVideoTime + Info.FrameDuration;
                    _lastVideoTime = t;
                    return Got.Video;
                }
                // EAGAIN = needs more input. Anything else once the input is used up means this decoder is finished.
                if (r == AVERROR_EOF || _inputEnded) _vDone = true;
            }
            if (_actx != null && _audioEnabled && !_aDone)
            {
                int r = avcodec_receive_frame(_actx, _aframe);
                if (r == 0) return Got.Audio;
                if (r == AVERROR_EOF || _inputEnded) _aDone = true;
            }
            if (_inputEnded)
            {
                if ((_vctx == null || _vDone) && (_actx == null || !_audioEnabled || _aDone)) return Got.End;
                continue;
            }

            int rr = av_read_frame(_fmt, _pkt);
            if (rr < 0)
            {
                if (rr == -EAGAIN) continue;
                _inputEnded = true; // end of file (or a read error): drain what the decoders still hold
                if (_vctx != null) avcodec_send_packet(_vctx, null);
                if (_actx != null && _audioEnabled) avcodec_send_packet(_actx, null);
                continue;
            }
            if (_pkt->stream_index == _vi && _vctx != null) avcodec_send_packet(_vctx, _pkt);
            else if (_pkt->stream_index == _ai && _audioEnabled) avcodec_send_packet(_actx, _pkt);
            av_packet_unref(_pkt);
        }
    }

    double FrameTime(AVFrame* f, AVRational tb)
    {
        long ts = f->best_effort_timestamp != AV_NOPTS_VALUE ? f->best_effort_timestamp : f->pts;
        return ts == AV_NOPTS_VALUE ? double.NaN : ts * Q(tb) - _start;
    }

    /// <summary>Time of the video frame <see cref="Next"/> just returned.</summary>
    public double VideoTime => _lastVideoTime;

    /// <summary>Time of the audio frame <see cref="Next"/> just returned (NaN if unknown).</summary>
    public double AudioTime => FrameTime(_aframe, _atb);

    /// <summary>
    /// Decodes to the frame on screen at <paramref name="time"/> (the last one starting at or before it), or with
    /// <paramref name="after"/> the first frame starting after it, and converts just that frame. When the target is a
    /// short way ahead of the previous result it keeps decoding from there instead of seeking (fast frame stepping).
    /// Returns false if nothing could be decoded or <paramref name="cancel"/> fired.
    /// </summary>
    public bool DecodeVideoAt(double time, bool after, VideoFrame dst, int outW, int outH, Func<bool>? cancel = null)
    {
        const double Eps = 1e-4;
        bool haveCur = _cur->buf[0] != null;
        if (haveCur && !after && time >= _curTime - Eps && _pushback->buf[0] != null && time < _pushbackTime - Eps)
        {
            Finish(dst, outW, outH); // still the same frame
            return true;
        }
        bool roll = haveCur && time >= _curTime - Eps && time - _curTime < 2.5;
        double backoff = 0;
        while (true)
        {
            if (!roll) Seek(Math.Max(0, time - backoff));
            // Rolling forward in "at" mode, the current frame is already a valid candidate (it starts before the target).
            bool haveCand = roll && !after;
            if (!haveCand) av_frame_unref(_cur);
            bool first = !roll;

            int n = 0;
            bool retry = false;
            while (true)
            {
                if ((++n & 7) == 0 && cancel?.Invoke() == true) return false;
                var got = Next();
                if (got == Got.Audio) continue;
                if (got == Got.End) break;
                double t = VideoTime;
                if (first)
                {
                    first = false;
                    // With B-frames a keyframe's decode time is earlier than its display time, so the container can seek
                    // to the keyframe just *after* the target. Then go further back.
                    if (t > time + Eps && time - backoff > 0.001 && backoff < 30)
                    {
                        backoff = backoff == 0 ? 1 : backoff * 3;
                        retry = true;
                        break;
                    }
                }
                if (t <= time + Eps)
                {
                    if (after) continue; // not past the current frame yet
                    av_frame_unref(_cur);
                    av_frame_ref(_cur, _vframe);
                    _curTime = t;
                    haveCand = true;
                    continue;
                }
                // First frame starting after the target.
                if (after || !haveCand)
                {
                    av_frame_unref(_cur);
                    av_frame_ref(_cur, _vframe);
                    _curTime = t;
                }
                else
                {
                    // Keep this next frame for the following step forward.
                    av_frame_unref(_pushback);
                    av_frame_ref(_pushback, _vframe);
                    _pushbackTime = t;
                }
                Finish(dst, outW, outH);
                return true;
            }
            if (!retry) break;
            roll = false;
        }
        if (_cur->buf[0] == null) return false;
        Finish(dst, outW, outH); // end of file: the last frame
        return true;
    }

    /// <summary>
    /// Quick preview while scrubbing: the keyframe at or before <paramref name="time"/> (only keyframes are decoded), or
    /// the exact frame when that is cheap because the target is just ahead of the previous result.
    /// </summary>
    public bool DecodeKeyframeNear(double time, VideoFrame dst, int outW, int outH)
    {
        if (_cur->buf[0] != null && time >= _curTime - 1e-4 && time - _curTime < 0.5)
            return DecodeVideoAt(time, false, dst, outW, outH);
        Seek(time);
        var skip = _vctx->skip_frame;
        _vctx->skip_frame = AVDiscard.AVDISCARD_NONKEY;
        try
        {
            while (true)
            {
                var got = Next();
                if (got == Got.End) return false;
                if (got != Got.Video) continue;
                Convert(_vframe, dst, outW, outH);
                dst.Time = VideoTime;
                return true;
            }
        }
        finally
        {
            _vctx->skip_frame = skip;
            // The decoder dropped the frames after this keyframe, so the next exact request must seek again.
            av_frame_unref(_cur);
            av_frame_unref(_pushback);
        }
    }

    void Finish(VideoFrame dst, int outW, int outH)
    {
        Convert(_cur, dst, outW, outH);
        dst.Time = _curTime;
    }

    /// <summary>Converts the last video frame from <see cref="Next"/>.</summary>
    public void ConvertVideo(VideoFrame dst, int outW, int outH)
    {
        Convert(_vframe, dst, outW, outH);
        dst.Time = VideoTime;
    }

    void Convert(AVFrame* f, VideoFrame dst, int outW, int outH)
    {
        int w = f->width, h = f->height, fmt = f->format;
        int cs = (int)f->colorspace, range = (int)f->color_range;
        var key = (w, h, fmt, cs, range, outW, outH);
        if (_sws == null || key != _swsKey)
        {
            sws_freeContext(_sws);
            _sws = sws_getContext(w, h, (AVPixelFormat)fmt, outW, outH, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_sws == null) throw new InvalidOperationException($"Can't convert {av_get_pix_fmt_name((AVPixelFormat)fmt)} pictures.");
            // YUV matrix and range from the frame (swscale otherwise assumes BT.601 limited range).
            int matrix = f->colorspace switch
            {
                AVColorSpace.AVCOL_SPC_BT709 => SWS_CS_ITU709,
                AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => SWS_CS_BT2020,
                AVColorSpace.AVCOL_SPC_SMPTE240M => SWS_CS_SMPTE240M,
                AVColorSpace.AVCOL_SPC_FCC => SWS_CS_FCC,
                AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => SWS_CS_ITU601,
                _ => h >= 720 ? SWS_CS_ITU709 : SWS_CS_ITU601,
            };
            bool full = f->color_range == AVColorRange.AVCOL_RANGE_JPEG || IsJpegFormat((AVPixelFormat)fmt);
            var coeffs = *(int_array4*)sws_getCoefficients(matrix);
            var outCoeffs = *(int_array4*)sws_getCoefficients(SWS_CS_DEFAULT);
            sws_setColorspaceDetails(_sws, coeffs, full ? 1 : 0, outCoeffs, 1, 0, 1 << 16, 1 << 16);
            _swsKey = key;
        }
        dst.EnsureSize(outW, outH);
        fixed (byte* p = dst.Data)
        {
            var dstData = new byte*[] { p, null, null, null };
            var dstStride = new[] { outW * 4, 0, 0, 0 };
            sws_scale(_sws, f->data.ToArray(), f->linesize.ToArray(), 0, h, dstData, dstStride);
        }
    }

    static bool IsJpegFormat(AVPixelFormat f) => f is AVPixelFormat.AV_PIX_FMT_YUVJ420P or AVPixelFormat.AV_PIX_FMT_YUVJ422P
        or AVPixelFormat.AV_PIX_FMT_YUVJ444P or AVPixelFormat.AV_PIX_FMT_YUVJ440P or AVPixelFormat.AV_PIX_FMT_YUVJ411P;

    /// <summary>
    /// Converts the last audio frame from <see cref="Next"/> to 48 kHz interleaved stereo 16-bit. The returned span is
    /// valid until the next call.
    /// </summary>
    public ReadOnlySpan<byte> ConvertAudio()
    {
        var f = _aframe;
        AVChannelLayout inLayout = f->ch_layout;
        if (inLayout.nb_channels == 0 || inLayout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
            av_channel_layout_default(&inLayout, Math.Max(1, f->ch_layout.nb_channels > 0 ? f->ch_layout.nb_channels : _actx->ch_layout.nb_channels));
        var key = (f->format, f->sample_rate, inLayout.u.mask, inLayout.nb_channels);
        if (_swr == null || key != _swrKey)
        {
            fixed (SwrContext** pp = &_swr) swr_free(pp);
            AVChannelLayout stereo;
            av_channel_layout_default(&stereo, 2);
            SwrContext* swr = null;
            Check(swr_alloc_set_opts2(&swr, &stereo, AVSampleFormat.AV_SAMPLE_FMT_S16, AudioRate,
                &inLayout, (AVSampleFormat)f->format, f->sample_rate, 0, null), "Couldn't set up audio conversion");
            Check(swr_init(swr), "Couldn't set up audio conversion");
            _swr = swr;
            _swrKey = key;
        }
        int maxOut = (int)av_rescale_rnd(swr_get_delay(_swr, f->sample_rate) + f->nb_samples, AudioRate, f->sample_rate, AVRounding.AV_ROUND_UP) + 32;
        if (_pcm.Length < maxOut * 4) _pcm = new byte[maxOut * 4];
        int got;
        fixed (byte* p = _pcm)
        {
            byte* outp = p;
            got = swr_convert(_swr, &outp, maxOut, f->extended_data, f->nb_samples);
        }
        return got > 0 ? new ReadOnlySpan<byte>(_pcm, 0, got * 4) : ReadOnlySpan<byte>.Empty;
    }

    /// <summary>Picture size to decode to: displayed aspect, longest side at most <paramref name="maxSide"/>.</summary>
    public (int W, int H) ScaledSize(int maxSide)
    {
        double w = Info.Width * Info.Sar, h = Info.Height;
        double s = Math.Min(1.0, maxSide / Math.Max(w, h));
        return (Math.Max(2, (int)Math.Round(w * s)), Math.Max(2, (int)Math.Round(h * s)));
    }

    /// <summary>Time of the keyframe a stream copy starting at <paramref name="time"/> really begins with.</summary>
    public double KeyframeAtOrBefore(double time)
    {
        Seek(time);
        try
        {
            for (int i = 0; i < 2000; i++)
            {
                if (av_read_frame(_fmt, _pkt) < 0) break;
                bool video = _pkt->stream_index == _vi;
                long ts = _pkt->pts != AV_NOPTS_VALUE ? _pkt->pts : _pkt->dts;
                av_packet_unref(_pkt);
                if (video && ts != AV_NOPTS_VALUE) return Math.Max(0, ts * Q(_vtb) - _start);
            }
            return time;
        }
        finally { Seek(time); }
    }

    // ───────────────────────── helpers ─────────────────────────

    static double Q(AVRational r) => r.den == 0 ? 0 : r.num / (double)r.den;

    static string? Str(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((IntPtr)p);

    static void Check(int result, string what)
    {
        if (result >= 0) return;
        throw new InvalidOperationException($"{what}: {ErrorText(result)}");
    }

    public static string ErrorText(int code)
    {
        const int Size = 256;
        var buf = stackalloc byte[Size];
        av_strerror(code, buf, Size);
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"error {code}";
    }

    public void Dispose()
    {
        sws_freeContext(_sws);
        _sws = null;
        fixed (SwrContext** pp = &_swr) swr_free(pp);
        fixed (AVFrame** p = &_vframe) av_frame_free(p);
        fixed (AVFrame** p = &_aframe) av_frame_free(p);
        fixed (AVFrame** p = &_cur) av_frame_free(p);
        fixed (AVFrame** p = &_pushback) av_frame_free(p);
        fixed (AVPacket** p = &_pkt) av_packet_free(p);
        fixed (AVCodecContext** p = &_vctx) avcodec_free_context(p);
        fixed (AVCodecContext** p = &_actx) avcodec_free_context(p);
        fixed (AVFormatContext** p = &_fmt) avformat_close_input(p);
    }
}
