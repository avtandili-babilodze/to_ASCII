using System.Diagnostics;
using System.Threading.Channels;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Imaging;
using AsciiVision.Core.Media;
using AsciiVision.Rendering.Glyphs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsciiVision.Export;

/// <summary>
/// Offline conversion of a video file to an ASCII MP4.
///
/// Unlike live preview, nothing here depends on wall-clock timing: ffmpeg decodes every frame at a
/// constant output rate, a bounded channel with <see cref="BoundedChannelFullMode.Wait"/> applies
/// backpressure (no frame is ever dropped), frames are converted strictly in order, and each one is
/// written to the encoder. The source audio is muxed from the original file; because the video
/// starts at t=0 with exactly one frame per 1/fps, audio and video stay in sync.
/// </summary>
public sealed class VideoExporter : IVideoExporter
{
    public const int MaxDecodeWidth = 1920;

    private readonly FfmpegLocator _locator;
    private readonly IAsciiProcessorFactory _processors;
    private readonly GlyphAtlasCache _atlases;
    private readonly ILogger _logger;

    public VideoExporter(FfmpegLocator locator, IAsciiProcessorFactory processors, GlyphAtlasCache atlases, ILogger<VideoExporter>? logger = null)
    {
        _locator = locator;
        _processors = processors;
        _atlases = atlases;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task<ExportResult> ExportAsync(ExportOptions options, AsciiSettings settings, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        var ffmpeg = _locator.Locate(settings.FfmpegPath) ?? throw new FfmpegNotFoundException();
        if (!ffmpeg.Capabilities.CanEncodeH264)
        {
            throw new FfmpegException("This FFmpeg build has no H.264 encoder (libx264 or h264_mf).");
        }

        var input = Path.GetFullPath(options.InputPath);
        var output = Path.GetFullPath(options.OutputPath);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The output file must be different from the input file.");
        }

        var info = await MediaProbe.ProbeAsync(ffmpeg, input, cancellationToken).ConfigureAwait(false);
        double fps = Math.Clamp(options.FramesPerSecond ?? info.FrameRate, 1, 240);
        var (decodeW, decodeH) = FfmpegArguments.DecodeSize(info.Width, info.Height, MaxDecodeWidth);

        double aspect = settings.AutoCharacterAspect ? _atlases.GetCellAspect(settings.FontFamily) : settings.CharacterAspect;
        var grid = GridCalculator.Compute(decodeW, decodeH, settings.EffectiveColumns, aspect);
        var compositor = new FrameCompositor(_atlases);
        var (outW, outH) = options.Resolution is { } r ? (r.Width + (r.Width & 1), r.Height + (r.Height & 1)) : compositor.NativeSize(grid, settings);
        // With an explicit resolution the font scales to fill it; natively it stays at the chosen size.
        double maxFont = options.Resolution is null ? settings.FontSize : 400;
        long expectedFrames = info.EstimatedFrames(fps);
        bool includeAudio = options.IncludeAudio && info.HasAudio && ffmpeg.Capabilities.HasAac;

        _logger.LogInformation("Exporting {Input} → {Output}: grid {Grid}, {W}x{H} @ {Fps} fps, audio {Audio}",
            input, output, grid, outW, outH, fps, includeAudio);

        var stopwatch = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        using var decoder = FfmpegProcess.Start(ffmpeg.FfmpegPath,
            FfmpegArguments.DecodeToRawBgra(info, ffmpeg.Capabilities, TimeSpan.Zero, fps, decodeW, decodeH, out _),
            redirectInput: false, redirectOutput: true);
        await using var encoder = FfmpegVideoEncoder.Start(ffmpeg, new FfmpegArguments.EncoderSettings(
            outW, outH, fps, options.Quality, output, includeAudio ? input : null));

        var channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        var reader = Task.Run(() => ReadFramesAsync(decoder, channel.Writer, decodeW, decodeH, fps, token), token);
        long done = 0;
        var outputBuffer = new byte[outW * outH * 4];
        using var processor = _processors.Create();
        long lastReport = 0;

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                using (frame)
                using (var ascii = processor.Process(frame, settings, grid))
                {
                    compositor.Compose(ascii, settings, outputBuffer, outW, outH, maxFont);
                }

                await encoder.WriteFrameAsync(outputBuffer, token).ConfigureAwait(false);
                done++;
                if (progress is not null && stopwatch.ElapsedMilliseconds - lastReport >= 100)
                {
                    lastReport = stopwatch.ElapsedMilliseconds;
                    progress.Report(new ExportProgress(done, Math.Max(expectedFrames, done), stopwatch.Elapsed, done / stopwatch.Elapsed.TotalSeconds));
                }
            }

            await reader.ConfigureAwait(false); // surfaces decode errors

            if (done == 0)
            {
                throw new FfmpegException("The video produced no frames.");
            }

            await encoder.CompleteAsync(token).ConfigureAwait(false);
            progress?.Report(new ExportProgress(done, done, stopwatch.Elapsed, done / stopwatch.Elapsed.TotalSeconds));
            _logger.LogInformation("Export finished: {Frames} frames in {Elapsed}", done, stopwatch.Elapsed);
            return new ExportResult(output, done, outW, outH, includeAudio, stopwatch.Elapsed);
        }
        catch
        {
            linked.Cancel();
            decoder.Kill();
            encoder.Abort();
            // Drain anything still queued so pooled buffers are returned.
            while (channel.Reader.TryRead(out var pending))
            {
                pending.Dispose();
            }

            try
            {
                await reader.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    private static async Task ReadFramesAsync(FfmpegProcess decoder, ChannelWriter<VideoFrame> writer, int width, int height, double fps, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            var stream = decoder.StandardOutput!;
            long index = 0;
            while (true)
            {
                var frame = VideoFrame.Rent(width, height, TimeSpan.FromSeconds(index / fps), index);
                if (!await ReadFullyAsync(stream, frame.Buffer.AsMemory(0, frame.ByteLength), token).ConfigureAwait(false))
                {
                    frame.Dispose();
                    break;
                }

                index++;
                try
                {
                    await writer.WriteAsync(frame, token).ConfigureAwait(false);
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            }

            int exit = await decoder.WaitForExitAsync(token).ConfigureAwait(false);
            if (exit != 0)
            {
                failure = FfmpegException.FromProcess("Decoding the input video", decoder, exit);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            writer.TryComplete(failure);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static async Task<bool> ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], token).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
