using System.Windows;
using LLMeta.App.Models;
using LLMeta.App.Services;
using LLMeta.App.Stores;
using LLMeta.App.Utils;
using LLMeta.App.ViewModels;
using Velopack;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;
using WinForms = System.Windows.Forms;

namespace LLMeta.App;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private OpenXrControllerInputService? _openXrControllerInputService;
    private DispatcherTimer? _openXrPollTimer;
    private string? _lastOpenXrStatus;
    private bool _isExitRequested;
    private AppLogger? _logger;

    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureDirectories();

        var logger = new AppLogger();
        _logger = logger;
        logger.Info("Startup begin.");

        try
        {
            DispatcherUnhandledException += (_, args) =>
            {
                logger.Error("DispatcherUnhandledException", args.Exception);
                System.Windows.MessageBox.Show(args.Exception.Message, "LLMeta Error");
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    logger.Error("UnhandledException", ex);
                }
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                logger.Error("UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            var settingsStore = new SettingsStore(logger);
            var settings = settingsStore.Load();
            var startupRegistryService = new StartupRegistryService();
            var mainViewModel = new MainViewModel(
                settings,
                settingsStore,
                startupRegistryService,
                logger
            );
            var openXrControllerInputService = new OpenXrControllerInputService();
            var initializeState = openXrControllerInputService.Initialize();
            mainViewModel.UpdateOpenXrControllerState(initializeState);
            logger.Info($"OpenXR input initialize: {initializeState.Status}");

            if (initializeState.IsInitialized)
            {
                _openXrControllerInputService = openXrControllerInputService;
                _openXrPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _openXrPollTimer.Tick += (_, _) =>
                {
                    if (_openXrControllerInputService is null)
                    {
                        return;
                    }

                    var state = _openXrControllerInputService.Poll();
                    mainViewModel.UpdateOpenXrControllerState(state);
                    if (_logger is not null && _lastOpenXrStatus != state.Status)
                    {
                        _lastOpenXrStatus = state.Status;
                        _logger.Info($"OpenXR input state: {state.Status}");
                    }
                };
                _openXrPollTimer.Start();
            }
            else
            {
                openXrControllerInputService.Dispose();
            }

            _mainViewModel = mainViewModel;

            if (settings.StartWithWindows)
            {
                var exePath = startupRegistryService.ResolveExecutablePath();
                startupRegistryService.Enable(exePath);
            }
            else
            {
                startupRegistryService.Disable();
            }

            _mainWindow = new MainWindow { DataContext = mainViewModel };
            MainWindow = _mainWindow;
            _mainWindow.Closing += OnMainWindowClosing;

            if (settings.StartMinimized)
            {
                _mainWindow.WindowState = WindowState.Minimized;
                _mainWindow.Hide();
            }
            else
            {
                _mainWindow.Show();
            }

            InitializeTrayIcon();

            logger.Info("Startup completed.");
        }
        catch (Exception ex)
        {
            logger.Error("Startup failed.", ex);
            System.Windows.MessageBox.Show(ex.Message, "LLMeta Error");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _openXrPollTimer?.Stop();
        _openXrPollTimer = null;
        _openXrControllerInputService?.Dispose();
        _openXrControllerInputService = null;
        _notifyIcon?.Dispose();
        base.OnExit(e);
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExitRequested)
        {
            return;
        }

        e.Cancel = true;
        _mainWindow?.Hide();
    }

    private void InitializeTrayIcon()
    {
        if (_mainViewModel is null)
        {
            return;
        }

        var menu = new WinForms.ContextMenuStrip();

        var openItem = new WinForms.ToolStripMenuItem("Open");
        openItem.Click += (_, _) => ShowMainWindow(forceShow: true);

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        var exePath = new StartupRegistryService().ResolveExecutablePath();
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = appIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "LLMeta",
        };
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                ShowMainWindow(forceShow: true);
            }
        };

        _mainViewModel.ExitRequested += (_, _) => ExitApp();
    }

    private void ShowMainWindow(bool forceShow)
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (forceShow)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void ExitApp()
    {
        _isExitRequested = true;
        _notifyIcon?.Dispose();
        _mainWindow?.Close();
        Shutdown();
    }
}
