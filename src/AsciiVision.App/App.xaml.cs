using System.Windows;
using System.Windows.Threading;
using AsciiVision.App.Services;
using AsciiVision.App.ViewModels;
using AsciiVision.App.Views;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Diagnostics;
using AsciiVision.Core.Media;
using AsciiVision.Export;
using AsciiVision.Processing;
using AsciiVision.Rendering.Console;
using AsciiVision.Rendering.Glyphs;
using AsciiVision.Rendering.Preview;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AsciiVision.App;

public partial class App : Application
{
    private readonly CommandLineOptions _options;
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    public App(CommandLineOptions options)
    {
        _options = options;
    }

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AsciiVision");

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AsciiVision", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var fileLogger = new FileLoggerProvider(LogDirectory);
        _services = ConfigureServices(fileLogger);
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("ASCII Vision starting on {OS}, .NET {Runtime}", Environment.OSVersion, Environment.Version);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);

        if (!CheckNativeDependencies(out var problem))
        {
            MessageBox.Show(problem, "ASCII Vision", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        if (_options.HasAutomation)
        {
            var viewModel = _services.GetRequiredService<MainViewModel>();
            Dispatcher.InvokeAsync(() => viewModel.ApplyCommandLineAsync(_options), DispatcherPriority.ApplicationIdle);
        }
    }

    private static ServiceProvider ConfigureServices(FileLoggerProvider fileLogger)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddDebug()
            .AddProvider(fileLogger));

        services.AddSingleton(sp => new SettingsService(Path.Combine(DataDirectory, "settings.json"), sp.GetRequiredService<ILogger<SettingsService>>()));
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<PerformanceMetrics>();
        services.AddSingleton<FfmpegLocator>();
        services.AddSingleton<AdaptiveQualityController>(_ => new AdaptiveQualityController());

        services.AddSingleton<WpfGlyphThread>();
        services.AddSingleton(sp => new GlyphAtlasCache(sp.GetRequiredService<WpfGlyphThread>()));
        services.AddSingleton<ICellGeometryProvider>(sp => sp.GetRequiredService<GlyphAtlasCache>());
        services.AddSingleton<IGlyphShapeProvider>(sp => sp.GetRequiredService<GlyphAtlasCache>());
        services.AddSingleton<IAsciiProcessorFactory>(sp => new AsciiProcessorFactory(sp.GetRequiredService<IGlyphShapeProvider>()));

        services.AddSingleton<PreviewRenderer>();
        services.AddSingleton<ConsoleRenderer>();
        services.AddSingleton<RecordingService>();
        services.AddSingleton<IRecordingService>(sp => sp.GetRequiredService<RecordingService>());
        services.AddSingleton<FrameImageExporter>();
        services.AddSingleton<IVideoExporter, VideoExporter>();

        services.AddSingleton(sp =>
        {
            var pipeline = new FramePipeline(
                sp.GetRequiredService<IAsciiProcessorFactory>(),
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ICellGeometryProvider>(),
                sp.GetRequiredService<PerformanceMetrics>(),
                sp.GetRequiredService<AdaptiveQualityController>(),
                sp.GetRequiredService<ILogger<FramePipeline>>())
            {
                Preview = sp.GetRequiredService<PreviewRenderer>(),
                Console = sp.GetRequiredService<ConsoleRenderer>(),
                Recorder = sp.GetRequiredService<RecordingService>(),
            };
            return pipeline;
        });

        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    /// <summary>Fails early with a readable message if the OpenCV native runtime cannot be loaded.</summary>
    private bool CheckNativeDependencies(out string problem)
    {
        try
        {
            _logger?.LogInformation("OpenCV {Version}", OpenCvSharp.Cv2.GetVersionString());
            problem = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException or EntryPointNotFoundException)
        {
            _logger?.LogCritical(ex, "OpenCV native runtime failed to load");
            problem = "The OpenCV native library (OpenCvSharpExtern.dll) could not be loaded.\n\n" +
                      "Make sure the application folder is complete and that the Microsoft Visual C++ 2015–2022 " +
                      "Redistributable (x64) and Media Foundation (on Windows N / Server editions) are installed.";
            return false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        var viewModel = _services?.GetService<MainViewModel>();
        if (viewModel is not null)
        {
            viewModel.Notify("Something went wrong: " + e.Exception.Message + $" (details in {LogDirectory})", NotificationSeverity.Error);
        }
        else
        {
            MessageBox.Show("Something went wrong: " + e.Exception.Message, "ASCII Vision", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            // Everything has been stopped by MainWindow's shutdown sequence; disposal is quick.
            var dispose = Task.Run(async () => await _services.DisposeAsync());
            if (!dispose.Wait(TimeSpan.FromSeconds(5)))
            {
                _logger?.LogWarning("Service disposal timed out");
            }
        }

        base.OnExit(e);
    }
}
