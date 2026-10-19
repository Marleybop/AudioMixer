using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioMixer.App.Services;
using AudioMixer.App.ViewModels;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace AudioMixer.App;

public partial class App : Application
{
    const string SingleInstanceName = "AudioMixer.SingleInstance";

    Mutex? _singleInstance;
    EventWaitHandle? _showSignal;
    AppHost? _host;
    MainViewModel? _viewModel;
    MainWindow? _window;
    TaskbarIcon? _tray;

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance only; a second launch just brings the first one's window up.
        _singleInstance = new Mutex(true, SingleInstanceName, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceName + ".Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "AudioMixer error", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        _host = new AppHost();
        _host.TryActivate();
        _viewModel = new MainViewModel(_host);
        _viewModel.Notify += (_, n) => _tray?.ShowNotification(n.Title, n.Message, NotificationIcon.Info);
        _viewModel.PropertyChanged += OnViewModelChanged;

        CreateTray();
        _window = new MainWindow(_viewModel);
        if (!e.Args.Contains("--tray"))
            ShowWindow();
    }

    void CreateTray()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open AudioMixer", ShowWindow));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Quit", Quit));

        _tray = new TaskbarIcon
        {
            ToolTipText = _viewModel!.TrayTooltip,
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _tray.LeftClickCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ShowWindow);
        UpdateTrayIcon();
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tray is null || _viewModel is null)
            return;
        if (e.PropertyName == nameof(MainViewModel.TrayTooltip))
            _tray.ToolTipText = _viewModel.TrayTooltip;
        if (e.PropertyName is nameof(MainViewModel.HeadsetBattery) or nameof(MainViewModel.HeadsetOn)
            or nameof(MainViewModel.MicMuted) or nameof(MainViewModel.BaseStationConnected))
            UpdateTrayIcon();
    }

    /// <summary>The tray icon shows the headset battery; orange when muted, grey when the headset is off.</summary>
    void UpdateTrayIcon()
    {
        var vm = _viewModel!;
        var on = vm.BaseStationConnected && vm.HeadsetOn;
        _tray!.IconSource = new GeneratedIconSource
        {
            Text = on ? vm.HeadsetBattery.ToString() : "–",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(!on ? Color.FromRgb(0x5A, 0x5F, 0x68)
                : vm.MicMuted ? Color.FromRgb(0xFF, 0x6A, 0x13)
                : vm.HeadsetBattery <= 25 ? Color.FromRgb(0xD9, 0x3F, 0x3F)
                : Color.FromRgb(0x24, 0x28, 0x30)),
            FontSize = vm.HeadsetBattery >= 100 ? 40 : 52,
            FontWeight = FontWeights.Bold,
        };
    }

    void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    void Quit()
    {
        IsExiting = true;
        _viewModel?.Shutdown();
        _window?.Close();
        _tray?.Dispose();
        _host?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!IsExiting)
        {
            _tray?.Dispose();
            _host?.Dispose();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
