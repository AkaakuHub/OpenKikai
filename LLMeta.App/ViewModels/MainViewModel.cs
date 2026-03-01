using System.ComponentModel;
using System.Windows.Input;
using LLMeta.App.Models;
using LLMeta.App.Services;
using LLMeta.App.Stores;
using LLMeta.App.Utils;
using LLMeta.App.ViewModels;

namespace LLMeta.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly StartupRegistryService _startupRegistryService;
    private readonly AppLogger _logger;

    private string _statusMessage = "Ready";
    private string _sampleText = string.Empty;

    public MainViewModel(
        AppSettings settings,
        SettingsStore settingsStore,
        StartupRegistryService startupRegistryService,
        AppLogger logger
    )
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _startupRegistryService = startupRegistryService;
        _logger = logger;
        _sampleText = settings.SampleText;

        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        OpenCommand = new RelayCommand(_ => _logger.Info("Open menu clicked"));
        ExitCommand = new RelayCommand(_ => ExitRequested?.Invoke(this, EventArgs.Empty));
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string SampleText
    {
        get => _sampleText;
        set => SetProperty(ref _sampleText, value);
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows != value)
            {
                _settings.StartWithWindows = value;
                RaisePropertyChanged();
                UpdateStartupRegistry();
            }
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set
        {
            if (_settings.StartMinimized != value)
            {
                _settings.StartMinimized = value;
                RaisePropertyChanged();
            }
        }
    }

    public ICommand SaveSettingsCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand ExitCommand { get; }

    public event EventHandler? ExitRequested;

    private void SaveSettings()
    {
        _settings.SampleText = SampleText;
        _settingsStore.Save(_settings);
        StatusMessage = "Settings saved!";
        _logger.Info("Settings saved.");
    }

    private void UpdateStartupRegistry()
    {
        try
        {
            if (_settings.StartWithWindows)
            {
                var exePath = _startupRegistryService.ResolveExecutablePath();
                _startupRegistryService.Enable(exePath);
            }
            else
            {
                _startupRegistryService.Disable();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to update startup registry.", ex);
        }
    }
}
