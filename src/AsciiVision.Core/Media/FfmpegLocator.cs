using System.Diagnostics;

namespace AsciiVision.Core.Media;

public sealed record FfmpegInstallation(string FfmpegPath, string? FfprobePath, string Version, FfmpegCapabilities Capabilities);

public sealed record FfmpegCapabilities(bool HasLibx264, bool HasH264MediaFoundation, bool HasAac, bool HasZscale, bool HasTonemap)
{
    public bool CanEncodeH264 => HasLibx264 || HasH264MediaFoundation;
}

/// <summary>
/// Finds ffmpeg.exe / ffprobe.exe. Search order: explicit path, application folder,
/// PATH, then common install locations (winget, Chocolatey, Scoop, C:\ffmpeg).
/// </summary>
public sealed class FfmpegLocator
{
    private readonly object _gate = new();
    private string? _cachedFor;
    private FfmpegInstallation? _cached;
    private bool _hasCached;

    public FfmpegInstallation? Locate(string? configuredPath)
    {
        lock (_gate)
        {
            if (_hasCached && string.Equals(_cachedFor, configuredPath, StringComparison.OrdinalIgnoreCase))
            {
                return _cached;
            }

            _cached = Probe(configuredPath);
            _cachedFor = configuredPath;
            _hasCached = true;
            return _cached;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _hasCached = false;
        }
    }

    public static IEnumerable<string> CandidatePaths(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var p = configuredPath.Trim().Trim('"');
            yield return Directory.Exists(p) ? Path.Combine(p, "ffmpeg.exe") : p;
        }

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "ffmpeg.exe");
        yield return Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe");
        yield return Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe");

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return candidate;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "ffmpeg.exe");
        yield return Path.Combine(user, "scoop", "shims", "ffmpeg.exe");
        yield return @"C:\ffmpeg\bin\ffmpeg.exe";
        yield return @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";
    }

    private static FfmpegInstallation? Probe(string? configuredPath)
    {
        foreach (var candidate in CandidatePaths(configuredPath))
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var version = RunAndCapture(candidate, ["-hide_banner", "-version"], TimeSpan.FromSeconds(10));
                if (version is null || !version.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var encoders = RunAndCapture(candidate, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(10)) ?? string.Empty;
                var filters = RunAndCapture(candidate, ["-hide_banner", "-filters"], TimeSpan.FromSeconds(10)) ?? string.Empty;
                var caps = new FfmpegCapabilities(
                    HasLibx264: encoders.Contains(" libx264 ", StringComparison.Ordinal),
                    HasH264MediaFoundation: encoders.Contains(" h264_mf ", StringComparison.Ordinal),
                    HasAac: encoders.Contains(" aac ", StringComparison.Ordinal),
                    HasZscale: filters.Contains(" zscale ", StringComparison.Ordinal),
                    HasTonemap: filters.Contains(" tonemap ", StringComparison.Ordinal));

                var probe = Path.Combine(Path.GetDirectoryName(candidate)!, "ffprobe.exe");
                var firstLine = version.Split('\n', 2)[0].Trim();
                return new FfmpegInstallation(Path.GetFullPath(candidate), File.Exists(probe) ? Path.GetFullPath(probe) : null, firstLine, caps);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static string? RunAndCapture(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            return null;
        }

        return stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
    }
}

public sealed class FfmpegNotFoundException : Exception
{
    public FfmpegNotFoundException()
        : base("FFmpeg was not found. Install FFmpeg (for example 'winget install Gyan.FFmpeg'), add it to PATH, " +
               "place ffmpeg.exe next to AsciiVision.exe, or choose its location in Settings → FFmpeg.")
    {
    }
}
