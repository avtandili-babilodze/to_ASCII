using System.ComponentModel;
using System.Windows;
using AsciiVision.App.Services;
using AsciiVision.App.ViewModels;

namespace AsciiVision.App.Views;

public partial class ExportWindow : Window
{
    public ExportWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChrome.UseDarkTitleBar(this);
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ExportViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
                vm.PropertyChanged += (_, args) =>
                {
                    // Close automatically once a cancellation requested by the close button has completed.
                    if (args.PropertyName == nameof(ExportViewModel.IsExporting) && !vm.IsExporting && vm.CloseWhenIdle)
                    {
                        Close();
                    }
                };
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is ExportViewModel vm && !vm.ConfirmClose())
        {
            e.Cancel = true;
        }
    }
}
