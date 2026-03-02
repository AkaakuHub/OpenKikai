using System.Windows;
using LLMeta.App.Models;
using LLMeta.App.Services;
using LLMeta.App.Stores;
using LLMeta.App.Utils;
using LLMeta.App.ViewModels;
using Velopack;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace LLMeta.App;

public partial class App : System.Windows.Application
{
    private const int AndroidBridgePort = 39090;
    private const int VideoBridgePort = 39100;
    private const int MaxVideoPacketsPerTick = 8;

    private OpenXrControllerInputService? _openXrControllerInputService;
    private AndroidInputBridgeTcpServerService? _androidInputBridgeTcpServerService;
    private VideoTcpFrameReceiverService? _videoTcpFrameReceiverService;
    private VideoH264DecodeService? _videoH264DecodeService;
    private readonly KeyboardInputEmulatorService _keyboardInputEmulatorService = new();
    private DispatcherTimer? _openXrPollTimer;
    private string? _lastOpenXrStatus;
    private uint _videoConnectionId;
    private bool _videoSyncReady;
    private DateTimeOffset _lastVideoPipelineLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastVideoSyncWaitLogAt = DateTimeOffset.MinValue;
    private uint _videoFramesObserved;
    private uint _videoFramesDroppedBeforeSync;
    private uint _videoDecodeCalls;
    private uint _videoDecodedFrames;
    private string _lastVideoDecodeStatus = "none";

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
            var mainViewModel = new MainViewModel(settings, settingsStore, logger);
            mainViewModel.OpenXrReinitializeRequested += () =>
            {
                var reinitializeState = ReinitializeOpenXr(logger);
                mainViewModel.UpdateOpenXrControllerState(reinitializeState);
                if (reinitializeState.IsInitialized)
                {
                    mainViewModel.StatusMessage =
                        "OpenXR reinitialized. Disable keyboard debug input to use real device.";
                }
                else
                {
                    mainViewModel.StatusMessage = "OpenXR reinitialize failed.";
                }
            };

            _androidInputBridgeTcpServerService = new AndroidInputBridgeTcpServerService(
                logger,
                AndroidBridgePort
            );
            _androidInputBridgeTcpServerService.Start();
            mainViewModel.BridgeStatus =
                _androidInputBridgeTcpServerService.StatusText + " (A-1: Android -> 10.0.2.2)";

            _videoTcpFrameReceiverService = new VideoTcpFrameReceiverService(
                logger,
                VideoBridgePort
            );
            _videoTcpFrameReceiverService.Start();
            _videoH264DecodeService = new VideoH264DecodeService(logger);
            _videoConnectionId = 0;
            _videoSyncReady = false;
            _lastVideoPipelineLogAt = DateTimeOffset.MinValue;
            _lastVideoSyncWaitLogAt = DateTimeOffset.MinValue;
            _videoFramesObserved = 0;
            _videoFramesDroppedBeforeSync = 0;
            _videoDecodeCalls = 0;
            _videoDecodedFrames = 0;
            _lastVideoDecodeStatus = "none";
            mainViewModel.VideoStatus =
                _videoTcpFrameReceiverService.StatusText + " (A-1: Android -> 10.0.2.2)";

            var initializeState = ReinitializeOpenXr(logger);
            mainViewModel.UpdateOpenXrControllerState(initializeState);

            MainWindow = new MainWindow { DataContext = mainViewModel };
            MainWindow.PreviewKeyDown += (_, args) =>
            {
                if (mainViewModel.IsKeyboardDebugMode)
                {
                    _keyboardInputEmulatorService.OnKeyDown(args.Key);
                }
            };
            MainWindow.PreviewKeyUp += (_, args) =>
            {
                if (mainViewModel.IsKeyboardDebugMode)
                {
                    _keyboardInputEmulatorService.OnKeyUp(args.Key);
                }
            };
            MainWindow.Show();

            if (_openXrPollTimer is null)
            {
                _openXrPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _openXrPollTimer.Tick += (_, _) =>
                {
                    OpenXrControllerState state;
                    if (mainViewModel.IsKeyboardDebugMode)
                    {
                        state = _keyboardInputEmulatorService.BuildState();
                        mainViewModel.ActiveInputSource = "Input source: Keyboard debug";
                    }
                    else if (_openXrControllerInputService is not null)
                    {
                        state = _openXrControllerInputService.Poll();
                        mainViewModel.ActiveInputSource = "Input source: OpenXR";
                    }
                    else
                    {
                        state = _keyboardInputEmulatorService.BuildUnavailableState(
                            "OpenXR is not initialized. Click Reinitialize OpenXR or enable keyboard debug input."
                        );
                        mainViewModel.ActiveInputSource = "Input source: unavailable";
                    }

                    mainViewModel.UpdateOpenXrControllerState(state);
                    if (_androidInputBridgeTcpServerService is not null)
                    {
                        _androidInputBridgeTcpServerService.UpdateLatestState(
                            state,
                            mainViewModel.IsKeyboardDebugMode
                        );
                        mainViewModel.BridgeStatus =
                            _androidInputBridgeTcpServerService.StatusText
                            + " (A-1: Android -> 10.0.2.2)";
                    }

                    if (_videoTcpFrameReceiverService is not null)
                    {
                        var packetsProcessedThisTick = 0;
                        while (
                            packetsProcessedThisTick < MaxVideoPacketsPerTick
                            && _videoTcpFrameReceiverService.TryGetLatestFrame(
                                out var encodedPacket
                            )
                        )
                        {
                            packetsProcessedThisTick++;
                            _videoFramesObserved++;
                            if (_videoH264DecodeService is not null)
                            {
                                if (encodedPacket.ConnectionId != _videoConnectionId)
                                {
                                    _videoH264DecodeService.Dispose();
                                    _videoH264DecodeService = new VideoH264DecodeService(logger);
                                    _videoConnectionId = encodedPacket.ConnectionId;
                                    _videoSyncReady = false;
                                    _lastVideoSyncWaitLogAt = DateTimeOffset.MinValue;
                                    _videoFramesObserved = 1;
                                    _videoFramesDroppedBeforeSync = 0;
                                    _videoDecodeCalls = 0;
                                    _videoDecodedFrames = 0;
                                    _lastVideoDecodeStatus = "none";
                                    logger.Info(
                                        "Video pipeline new connection: "
                                            + $"conn={_videoConnectionId} seq={encodedPacket.Sequence} flags={encodedPacket.Flags} payload={encodedPacket.Payload.Length}"
                                    );
                                }

                                var shouldDecode = true;
                                if (!_videoSyncReady)
                                {
                                    if (encodedPacket.HasCodecConfig && encodedPacket.IsKeyFrame)
                                    {
                                        _videoSyncReady = true;
                                        logger.Info(
                                            "Video sync AU accepted: "
                                                + $"conn={_videoConnectionId} seq={encodedPacket.Sequence} flags={encodedPacket.Flags} payload={encodedPacket.Payload.Length}"
                                        );
                                    }
                                    else
                                    {
                                        _videoFramesDroppedBeforeSync++;
                                        var now = DateTimeOffset.UtcNow;
                                        if (
                                            _lastVideoSyncWaitLogAt == DateTimeOffset.MinValue
                                            || (now - _lastVideoSyncWaitLogAt).TotalSeconds >= 1
                                        )
                                        {
                                            _lastVideoSyncWaitLogAt = now;
                                            logger.Info(
                                                "Video waiting sync AU: "
                                                    + $"conn={_videoConnectionId} seq={encodedPacket.Sequence} flags={encodedPacket.Flags} hasCodecConfig={encodedPacket.HasCodecConfig} isKeyFrame={encodedPacket.IsKeyFrame}"
                                            );
                                        }
                                        mainViewModel.VideoStatus =
                                            _videoTcpFrameReceiverService.StatusText
                                            + " | decode: waiting sync AU (hasCodecConfig=1 && isKeyFrame=1)"
                                            + " (A-1: Android -> 10.0.2.2)";
                                        shouldDecode = false;
                                    }
                                }

                                if (shouldDecode)
                                {
                                    _videoDecodeCalls++;
                                    var decodeStatus = _videoH264DecodeService.Decode(
                                        encodedPacket
                                    );
                                    _lastVideoDecodeStatus = decodeStatus;
                                    if (
                                        _videoH264DecodeService.TryGetLatestFrame(
                                            out var decodedFrame
                                        ) && _openXrControllerInputService is not null
                                    )
                                    {
                                        _videoDecodedFrames++;
                                        _openXrControllerInputService.SetLatestDecodedSbsFrame(
                                            decodedFrame
                                        );
                                    }

                                    mainViewModel.VideoStatus =
                                        _videoTcpFrameReceiverService.StatusText
                                        + " | decode: "
                                        + decodeStatus
                                        + " (A-1: Android -> 10.0.2.2)";
                                }
                            }
                        }

                        if (!mainViewModel.VideoStatus.Contains("decode:"))
                        {
                            mainViewModel.VideoStatus =
                                _videoTcpFrameReceiverService.StatusText
                                + " (A-1: Android -> 10.0.2.2)";
                        }
                    }

                    if (_videoTcpFrameReceiverService is not null)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (
                            _lastVideoPipelineLogAt == DateTimeOffset.MinValue
                            || (now - _lastVideoPipelineLogAt).TotalSeconds >= 2
                        )
                        {
                            _lastVideoPipelineLogAt = now;
                            var stats = _videoTcpFrameReceiverService.GetStatsSnapshot();
                            logger.Info(
                                "Video pipeline stats: "
                                    + $"conn={_videoConnectionId} connected={stats.IsConnected} syncReady={_videoSyncReady} "
                                    + $"rxFrames={_videoFramesObserved} waitingSyncDrop={_videoFramesDroppedBeforeSync} "
                                    + $"decodeCalls={_videoDecodeCalls} decodedFrames={_videoDecodedFrames} "
                                    + $"lastSeq={stats.LastSequence} lastPayload={stats.LastPayloadSize} latencyMs={stats.LastLatencyMs} "
                                    + $"lastDecodeStatus={_lastVideoDecodeStatus}"
                            );
                        }
                    }

                    if (_lastOpenXrStatus != state.Status)
                    {
                        _lastOpenXrStatus = state.Status;
                        logger.Info($"OpenXR input state: {state.Status}");
                    }
                };
                _openXrPollTimer.Start();
            }

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
        _androidInputBridgeTcpServerService?.Dispose();
        _androidInputBridgeTcpServerService = null;
        _videoTcpFrameReceiverService?.Dispose();
        _videoTcpFrameReceiverService = null;
        _videoH264DecodeService?.Dispose();
        _videoH264DecodeService = null;
        _videoConnectionId = 0;
        _videoSyncReady = false;
        base.OnExit(e);
    }

    private OpenXrControllerState ReinitializeOpenXr(AppLogger logger)
    {
        _openXrControllerInputService?.Dispose();
        _openXrControllerInputService = null;

        var openXrControllerInputService = new OpenXrControllerInputService();
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
