using System.Globalization;
using AsciiVision.Core.Configuration;

namespace AsciiVision.Core.Media;

/// <summary>Builds argument lists shared by playback, export and recording.</summary>
public static class FfmpegArguments
{
    /// <summary>Picks a decode size no wider than <paramref name="maxWidth"/>, preserving aspect, with even dimensions.</summary>
    public static (int Width, int Height) DecodeSize(int sourceWidth, int sourceHeight, int maxWidth)
    {
        int width = Math.Min(sourceWidth, maxWidth);
        int height = (int)Math.Round(sourceHeight * (double)width / sourceWidth);
        width = Math.Max(2, width & ~1);
        height = Math.Max(2, height & ~1);
        return (width, height);
    }

    /// <summary>
    /// Video filter chain: optional HDR→SDR tone mapping, constant frame rate, scale, BGRA output.
    /// Forcing CFR means frame <c>i</c> is exactly at <c>start + i / fps</c>, which keeps playback
    /// and export timing deterministic even for variable-frame-rate inputs.
    /// </summary>
    public static string VideoFilter(MediaInfo info, FfmpegCapabilities caps, double fps, int width, int height, out bool toneMapped)
    {
        var parts = new List<string>();
        toneMapped = false;
        if (info.IsHdr && caps.HasZscale && caps.HasTonemap)
        {
            parts.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p");
            toneMapped = true;
        }

        parts.Add("fps=" + fps.ToString("0.######", CultureInfo.InvariantCulture));
        parts.Add($"scale={width}:{height}:flags=area");
        parts.Add("format=bgra");
        return string.Join(",", parts);
    }

    public static List<string> DecodeToRawBgra(MediaInfo info, FfmpegCapabilities caps, TimeSpan start, double fps, int width, int height, out bool toneMapped)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (start > TimeSpan.Zero)
        {
            // Input seeking: fast, and frame-accurate when transcoding (accurate_seek is the default).
            args.Add("-ss");
            args.Add(start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.AddRange(["-i", info.Path, "-map", "0:v:0", "-an", "-sn", "-dn"]);
        args.AddRange(["-vf", VideoFilter(info, caps, fps, width, height, out toneMapped)]);
        args.AddRange(["-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"]);
        return args;
    }

    public sealed record EncoderSettings(
        int Width,
        int Height,
        double FramesPerSecond,
        EncodingQuality Quality,
        string OutputPath,
        string? AudioSourcePath = null);

    /// <summary>Raw BGRA frames on stdin → H.264 MP4 (optionally muxing audio from another file).</summary>
    public static List<string> EncodeRawBgraToMp4(EncoderSettings s, FfmpegCapabilities caps)
    {
        if (s.Width % 2 != 0 || s.Height % 2 != 0)
        {
            throw new ArgumentException("H.264 with 4:2:0 chroma requires even frame dimensions.");
        }

        var fps = s.FramesPerSecond.ToString("0.######", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-video_size", $"{s.Width}x{s.Height}", "-framerate", fps,
            "-i", "pipe:0",
        };

        bool audio = s.AudioSourcePath is not null && caps.HasAac;
        if (audio)
        {
            args.AddRange(["-i", s.AudioSourcePath!]);
        }

        args.AddRange(["-map", "0:v:0"]);
        if (audio)
        {
            args.AddRange(["-map", "1:a:0?", "-c:a", "aac", "-b:a", "192k"]);
        }

        if (caps.HasLibx264)
        {
            var (crf, preset) = s.Quality switch
            {
                EncodingQuality.High => ("18", "medium"),
                EncodingQuality.Small => ("28", "faster"),
                _ => ("23", "veryfast"),
            };
            // Character art is high-contrast; the 'animation' tune keeps glyph edges sharp.
            args.AddRange(["-c:v", "libx264", "-preset", preset, "-crf", crf, "-tune", "animation"]);
        }
        else if (caps.HasH264MediaFoundation)
        {
            double pixelsPerSecond = s.Width * (double)s.Height * s.FramesPerSecond;
            double bitsPerPixel = s.Quality switch { EncodingQuality.High => 0.25, EncodingQuality.Small => 0.06, _ => 0.12 };
            long bitrate = Math.Clamp((long)(pixelsPerSecond * bitsPerPixel), 1_000_000, 80_000_000);
            args.AddRange(["-c:v", "h264_mf", "-b:v", bitrate.ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            throw new FfmpegException("This FFmpeg build has no H.264 encoder (libx264 or h264_mf). Install a full FFmpeg build such as the gyan.dev or BtbN builds.");
        }

        args.AddRange(["-pix_fmt", "yuv420p", "-r", fps, "-movflags", "+faststart"]);
        if (audio)
        {
            args.Add("-shortest");
        }

        args.Add(s.OutputPath);
        return args;
    }
}
