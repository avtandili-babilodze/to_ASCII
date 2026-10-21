using System.Globalization;
using System.Text.Json;

namespace AsciiVision.Core.Media;

public sealed record MediaInfo
{
    public required string Path { get; init; }

    /// <summary>Display size (after applying rotation metadata, which ffmpeg auto-applies when decoding).</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public TimeSpan Duration { get; init; }
    public long? FrameCount { get; init; }
    public bool HasAudio { get; init; }
    public string? VideoCodec { get; init; }
    public string? ColorTransfer { get; init; }
    public string? PixelFormat { get; init; }

    /// <summary>PQ (HDR10) or HLG transfer: needs tone mapping to look right as SDR.</summary>
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    public long EstimatedFrames(double fps) =>
        Duration > TimeSpan.Zero ? Math.Max(1, (long)Math.Round(Duration.TotalSeconds * fps)) : FrameCount ?? 0;
}

/// <summary>Reads stream metadata with ffprobe (or ffmpeg's banner as a fallback).</summary>
public static class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(FfmpegInstallation ffmpeg, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The video file does not exist.", path);
        }

        if (ffmpeg.FfprobePath is null)
        {
            throw new FfmpegException("ffprobe.exe was not found next to ffmpeg.exe; it is required to read video information.");
        }

        using var process = FfmpegProcess.Start(ffmpeg.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path],
            redirectInput: false, redirectOutput: true);

        string json;
        using (var reader = new StreamReader(process.StandardOutput!))
        {
            json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        int exit = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (exit != 0 || string.IsNullOrWhiteSpace(json))
        {
            throw FfmpegException.FromProcess("Reading the video", process, exit);
        }

        return Parse(path, json);
    }

    public static MediaInfo Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement? video = null;
        bool hasAudio = false;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                bool attachedPic = s.TryGetProperty("disposition", out var disp)
                                   && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && video is null && !attachedPic)
                {
                    video = s;
                }
                else if (type == "audio")
                {
                    hasAudio = true;
                }
            }
        }

        if (video is not { } v)
        {
            throw new FfmpegException("The file does not contain a video stream.");
        }

        int width = v.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        int height = v.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        if (width <= 0 || height <= 0)
        {
            throw new FfmpegException("The video stream has no valid frame size.");
        }

        int rotation = ReadRotation(v);
        if (Math.Abs(rotation) % 180 == 90)
        {
            (width, height) = (height, width);
        }

        double fps = ParseRate(GetString(v, "avg_frame_rate"));
        if (fps <= 0 || fps > 1000)
        {
            fps = ParseRate(GetString(v, "r_frame_rate"));
        }

        if (fps <= 0 || fps > 1000)
        {
            fps = 30;
        }

        double duration = ParseDouble(GetString(v, "duration"));
        if (duration <= 0 && root.TryGetProperty("format", out var format))
        {
            duration = ParseDouble(GetString(format, "duration"));
        }

        long? frames = long.TryParse(GetString(v, "nb_frames"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nb) && nb > 0 ? nb : null;

        return new MediaInfo
        {
            Path = path,
            Width = width,
            Height = height,
            FrameRate = fps,
            Duration = duration > 0 ? TimeSpan.FromSeconds(duration) : TimeSpan.Zero,
            FrameCount = frames,
            HasAudio = hasAudio,
            VideoCodec = GetString(v, "codec_name"),
            ColorTransfer = GetString(v, "color_transfer"),
            PixelFormat = GetString(v, "pix_fmt"),
        };
    }

    private static int ReadRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var list))
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var r) && r.TryGetInt32(out var deg))
                {
                    return deg;
                }
            }
        }

        if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate)
            && int.TryParse(rotate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tagDeg))
        {
            return tagDeg;
        }

        return 0;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString() : null;

    public static double ParseRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate))
        {
            return 0;
        }

        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0)
        {
            return num / den;
        }

        return ParseDouble(rate);
    }

    private static double ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : 0;
}
