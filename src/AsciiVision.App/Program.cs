using AsciiVision.Rendering.Console;

namespace AsciiVision.App;

public static class Program
{
    /// <summary>
    /// One executable, two roles: normally the WPF app; with <c>--console-host</c> it becomes the
    /// console output process (see <see cref="ConsoleHost"/>), so no second binary has to be shipped.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains(ConsoleHost.Argument))
        {
            return ConsoleHost.Run(args);
        }

        var app = new App(CommandLineOptions.Parse(args));
        app.InitializeComponent();
        return app.Run();
    }
}
