using OpenKikai.App.Models;
using OpenKikai.App.Services;
using OpenKikai.App.Utils;

namespace OpenKikai.App;

public partial class App
{
    private OpenXrControllerState ReinitializeOpenXr(
        AppLogger logger,
        string preferredSwapchainFormat,
        string preferredGraphicsAdapter,
        string preferredGraphicsBackend
    )
    {
        _openXrControllerInputService?.Dispose();
        _openXrControllerInputService = null;

        var openXrControllerInputService = new OpenXrControllerInputService(
            preferredSwapchainFormat,
            preferredGraphicsAdapter,
            preferredGraphicsBackend,
            logger
        );
        var initializeState = openXrControllerInputService.Initialize();
        logger.Info($"OpenXR input initialize: {initializeState.Status}");

        if (initializeState.IsInitialized)
        {
            _openXrControllerInputService = openXrControllerInputService;
        }
        else
        {
            openXrControllerInputService.Dispose();
        }

        return initializeState;
    }
}
