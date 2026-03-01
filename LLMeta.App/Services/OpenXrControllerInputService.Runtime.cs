using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private Result PollEvents()
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        while (true)
        {
            var eventBuffer = new EventDataBuffer { Type = StructureType.EventDataBuffer };
            var result = _xr.PollEvent(_instance, ref eventBuffer);
            if (result == Result.EventUnavailable)
            {
                return result;
            }

            if (result != Result.Success)
            {
                return result;
            }

            if (eventBuffer.Type != StructureType.EventDataSessionStateChanged)
            {
                continue;
            }

            var stateChanged = *(EventDataSessionStateChanged*)&eventBuffer;
            if (stateChanged.Session.Handle != _session.Handle)
            {
                continue;
            }

            _sessionState = stateChanged.State;
            if (_sessionState == SessionState.Ready)
            {
                var beginInfo = new SessionBeginInfo
                {
                    Type = StructureType.SessionBeginInfo,
                    PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo,
                };
                var beginResult = _xr.BeginSession(_session, ref beginInfo);
                _isSessionRunning = beginResult == Result.Success;
            }
            else if (_sessionState == SessionState.Stopping)
            {
                _ = _xr.EndSession(_session);
                _isSessionRunning = false;
            }
            else if (
                _sessionState == SessionState.Exiting
                || _sessionState == SessionState.LossPending
            )
            {
                _isSessionRunning = false;
            }
        }
    }

    private Result PumpFrame()
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var waitInfo = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        var frameState = new FrameState { Type = StructureType.FrameState };
        var waitResult = _xr.WaitFrame(_session, ref waitInfo, ref frameState);
        if (waitResult != Result.Success)
        {
            return waitResult;
        }

        var beginInfo = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
        var beginResult = _xr.BeginFrame(_session, ref beginInfo);
        if (beginResult != Result.Success)
        {
            return beginResult;
        }

        var endInfo = new FrameEndInfo
        {
            Type = StructureType.FrameEndInfo,
            DisplayTime = frameState.PredictedDisplayTime,
            EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
            LayerCount = 0,
            Layers = (CompositionLayerBaseHeader**)0,
        };
        return _xr.EndFrame(_session, ref endInfo);
    }

    private static bool CanSyncActionsInCurrentState(SessionState state)
    {
        return state == SessionState.Focused;
    }

    private bool GetBooleanActionState(XrAction action)
    {
        if (_xr is null)
        {
            return false;
        }

        var getInfo = new ActionStateGetInfo
        {
            Type = StructureType.ActionStateGetInfo,
            Action = action,
            SubactionPath = XR.NullPath,
        };
        var state = new ActionStateBoolean { Type = StructureType.ActionStateBoolean };
        var result = _xr.GetActionStateBoolean(_session, ref getInfo, ref state);
        if (result != Result.Success || state.IsActive == 0)
        {
            return false;
        }

        return state.CurrentState != 0;
    }

    private Vector2f GetVector2ActionState(XrAction action)
    {
        if (_xr is null)
        {
            return new Vector2f();
        }

        var getInfo = new ActionStateGetInfo
        {
            Type = StructureType.ActionStateGetInfo,
            Action = action,
            SubactionPath = XR.NullPath,
        };
        var state = new ActionStateVector2f { Type = StructureType.ActionStateVector2f };
        var result = _xr.GetActionStateVector2(_session, ref getInfo, ref state);
        if (result != Result.Success || state.IsActive == 0)
        {
            return new Vector2f();
        }

        return state.CurrentState;
    }
}
