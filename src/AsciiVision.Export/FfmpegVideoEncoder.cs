using AsciiVision.Core.Media;

namespace AsciiVision.Export;

/// <summary>
/// Streams raw BGRA frames into an ffmpeg process that encodes H.264 MP4.
/// Frame size, pixel format and rate are declared on the command line, so every write must be
/// exactly <see cref="FrameBytes"/> long. Output goes to a temporary file that is moved into place
/// only after ffmpeg exits successfully, so a failed or cancelled encode never leaves a broken MP4
/// at the destination.
/// </summary>
public sealed class FfmpegVideoEncoder : IAsyncDisposable
{
    private readonly FfmpegProcess _process;
    private readonly Stream _input;
    private readonly string _temporaryPath;
    private readonly string _finalPath;
    private bool _completed;
    private bool _aborted;

    private FfmpegVideoEncoder(FfmpegProcess process, FfmpegArguments.EncoderSettings settings, string temporaryPath, string finalPath)
    {
        _process = process;
        _input = process.StandardInput!;
        Settings = settings;
        _temporaryPath = temporaryPath;
        _finalPath = finalPath;
    }

    public FfmpegArguments.EncoderSettings Settings { get; }

    public int FrameBytes => Settings.Width * Settings.Height * 4;

    public long FramesWritten { get; private set; }

    public static FfmpegVideoEncoder Start(FfmpegInstallation ffmpeg, FfmpegArguments.EncoderSettings settings)
    {
        var finalPath = Path.GetFullPath(settings.OutputPath);
        var directory = Path.GetDirectoryName(finalPath) ?? throw new ArgumentException("Invalid output path.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.partial.mp4");

        var args = FfmpegArguments.EncodeRawBgraToMp4(settings with { OutputPath = temporaryPath }, ffmpeg.Capabilities);
        var process = FfmpegProcess.Start(ffmpeg.FfmpegPath, args, redirectInput: true, redirectOutput: false);
        return new FfmpegVideoEncoder(process, settings, temporaryPath, finalPath);
    }

    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> bgra, CancellationToken cancellationToken)
    {
        if (bgra.Length != FrameBytes)
        {
            throw new ArgumentException($"Expected {FrameBytes} bytes per frame, got {bgra.Length}.", nameof(bgra));
        }

        try
        {
            await _input.WriteAsync(bgra, cancellationToken).ConfigureAwait(false);
            FramesWritten++;
        }
        catch (IOException ex)
        {
            // ffmpeg closed its input: it failed; report its own diagnostics.
            _process.WaitForExit(TimeSpan.FromSeconds(2));
            throw new FfmpegException(FfmpegException.FromProcess("Video encoding", _process).Message, ex);
        }
    }

    /// <summary>Closes the input so ffmpeg flushes and finalizes the file, then moves it into place.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed || _aborted)
        {
            return;
        }

        try
        {
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        _input.Dispose();
        int exit = await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            TryDelete(_temporaryPath);
            throw FfmpegException.FromProcess("Video encoding", _process, exit);
        }

        if (FramesWritten == 0)
        {
            TryDelete(_temporaryPath);
            throw new FfmpegException("No frames were encoded.");
        }

        File.Move(_temporaryPath, _finalPath, overwrite: true);
        _completed = true;
    }

    /// <summary>Stops ffmpeg immediately and removes the partial output.</summary>
    public void Abort()
    {
        if (_completed || _aborted)
        {
            return;
        }

        _aborted = true;
        _process.Kill();
        _process.WaitForExit(TimeSpan.FromSeconds(5));
        TryDelete(_temporaryPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            Abort();
        }

        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
