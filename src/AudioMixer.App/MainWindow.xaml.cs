using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AudioMixer.App.ViewModels;

namespace AudioMixer.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => UseDarkTitleBar();
        // Live meters only run while the window is on screen.
        IsVisibleChanged += (_, e) => viewModel.IsViewVisible = (bool)e.NewValue && WindowState != WindowState.Minimized;
        StateChanged += (_, _) => viewModel.IsViewVisible = IsVisible && WindowState != WindowState.Minimized;
    }

    void OnLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    void UseDarkTitleBar()
    {
        const int DwmwaUseImmersiveDarkMode = 20;
        var enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Closing the window keeps AudioMixer running in the tray.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!((App)Application.Current).IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
