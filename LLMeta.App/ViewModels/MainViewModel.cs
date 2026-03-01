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
    private string _openXrInputStatus = "OpenXR input: not initialized";
    private string _leftControllerState = "Left: -";
    private string _rightControllerState = "Right: -";

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

    public string OpenXrInputStatus
    {
        get => _openXrInputStatus;
        set => SetProperty(ref _openXrInputStatus, value);
    }

    public string LeftControllerState
    {
        get => _leftControllerState;
        set => SetProperty(ref _leftControllerState, value);
    }

    public string RightControllerState
    {
        get => _rightControllerState;
        set => SetProperty(ref _rightControllerState, value);
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

    public void UpdateOpenXrControllerState(OpenXrControllerState state)
    {
        OpenXrInputStatus = $"OpenXR: {state.Status}";
        LeftControllerState =
            $"Left Stick ({state.LeftStickX:0.00}, {state.LeftStickY:0.00}) Click:{ToOnOff(state.LeftStickClickPressed)} | X:{ToOnOff(state.LeftXPressed)} Y:{ToOnOff(state.LeftYPressed)} | Trigger:{state.LeftTriggerValue:0.00} | Grip:{state.LeftGripValue:0.00}";
        RightControllerState =
            $"Right Stick ({state.RightStickX:0.00}, {state.RightStickY:0.00}) Click:{ToOnOff(state.RightStickClickPressed)} | A:{ToOnOff(state.RightAPressed)} B:{ToOnOff(state.RightBPressed)} | Trigger:{state.RightTriggerValue:0.00} | Grip:{state.RightGripValue:0.00}";
    }

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

    private static string ToOnOff(bool value)
    {
        return value ? "ON" : "OFF";
    }
}
