using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace AsciiVision.App.Services;

public interface IDialogService
{
    string? OpenFile(string title, string filter, string? initialDirectory = null);

    string? SaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    bool TrySetClipboardText(string text);

    void ShowInExplorer(string path);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public string? OpenFile(string title, string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, OverwritePrompt = true, AddExtension = true };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool TrySetClipboardText(string text)
    {
        // The clipboard can be briefly locked by another process (CLIPBRD_E_CANT_OPEN); retry a few times.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }

        return false;
    }

    public void ShowInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
