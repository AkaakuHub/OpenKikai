using OpenKikai.App.Models;
using OpenKikai.App.Services.WindowCapture;
using OpenKikai.App.Stores;
using OpenKikai.App.ViewModels;

namespace OpenKikai.App;

public partial class App
{
    private static void SaveCaptureTargetSelection(
        AppSettings settings,
        SettingsStore settingsStore,
        SavedCaptureTarget? savedCaptureTarget
    )
    {
        settings.SavedCaptureTarget = savedCaptureTarget;
        settingsStore.Save(settings);
    }

    private void RestoreCaptureTargetIfAvailable(
        AppSettings settings,
        CaptureTargetRestoreService captureTargetRestoreService,
        MainViewModel mainViewModel
    )
    {
        if (_windowCaptureService is null || settings.SavedCaptureTarget is null)
        {
            return;
        }

        var restoredItem = captureTargetRestoreService.TryRestore(settings.SavedCaptureTarget);
        if (restoredItem is null)
        {
            return;
        }

        if (_windowCaptureService.StartCapture(restoredItem))
        {
            mainViewModel.CaptureStatus = _windowCaptureService.GetStatusText();
            mainViewModel.StatusMessage = "Previous capture target restored.";
        }
    }
}
