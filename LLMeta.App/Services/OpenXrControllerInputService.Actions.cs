using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private Result InitializeActions()
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var actionSetCreateInfo = new ActionSetCreateInfo
        {
            Type = StructureType.ActionSetCreateInfo,
            Priority = 0,
        };
        var actionSetName = actionSetCreateInfo.ActionSetName;
        WriteFixedUtf8(actionSetName, (int)XR.MaxActionSetNameSize, "llmeta_input");
        var localizedActionSetName = actionSetCreateInfo.LocalizedActionSetName;
        WriteFixedUtf8(
            localizedActionSetName,
            (int)XR.MaxLocalizedActionSetNameSize,
            "LLMeta Input"
        );
        var createActionSetResult = _xr.CreateActionSet(
            _instance,
            ref actionSetCreateInfo,
            ref _actionSet
        );
        if (createActionSetResult != Result.Success)
        {
            return createActionSetResult;
        }

        var createLeftStickResult = CreateAction(
            _actionSet,
            "left_stick",
            "Left Stick",
            ActionType.Vector2fInput,
            ref _leftStickAction
        );
        if (createLeftStickResult != Result.Success)
        {
            return createLeftStickResult;
        }

        var createRightStickResult = CreateAction(
            _actionSet,
            "right_stick",
            "Right Stick",
            ActionType.Vector2fInput,
            ref _rightStickAction
        );
        if (createRightStickResult != Result.Success)
        {
            return createRightStickResult;
        }

        var createLeftXResult = CreateAction(
            _actionSet,
            "left_x",
            "Left X",
            ActionType.BooleanInput,
            ref _leftXAction
        );
        if (createLeftXResult != Result.Success)
        {
            return createLeftXResult;
        }

        var createLeftYResult = CreateAction(
            _actionSet,
            "left_y",
            "Left Y",
            ActionType.BooleanInput,
            ref _leftYAction
        );
        if (createLeftYResult != Result.Success)
        {
            return createLeftYResult;
        }

        var createRightAResult = CreateAction(
            _actionSet,
            "right_a",
            "Right A",
            ActionType.BooleanInput,
            ref _rightAAction
        );
        if (createRightAResult != Result.Success)
        {
            return createRightAResult;
        }

        var createRightBResult = CreateAction(
            _actionSet,
            "right_b",
            "Right B",
            ActionType.BooleanInput,
            ref _rightBAction
        );
        if (createRightBResult != Result.Success)
        {
            return createRightBResult;
        }

        var bindings = new ActionSuggestedBinding[6];
        var pathResult = StringToPath(
            _instance,
            "/user/hand/left/input/thumbstick",
            out var leftStickPath
        );
        if (pathResult != Result.Success)
        {
            return pathResult;
        }
        bindings[0] = new ActionSuggestedBinding
        {
            Action = _leftStickAction,
            Binding = leftStickPath,
        };

        pathResult = StringToPath(
            _instance,
            "/user/hand/right/input/thumbstick",
            out var rightStickPath
        );
        if (pathResult != Result.Success)
        {
            return pathResult;
        }
        bindings[1] = new ActionSuggestedBinding
        {
            Action = _rightStickAction,
            Binding = rightStickPath,
        };

        pathResult = StringToPath(_instance, "/user/hand/left/input/x/click", out var leftXPath);
        if (pathResult != Result.Success)
        {
            return pathResult;
        }
        bindings[2] = new ActionSuggestedBinding { Action = _leftXAction, Binding = leftXPath };

        pathResult = StringToPath(_instance, "/user/hand/left/input/y/click", out var leftYPath);
        if (pathResult != Result.Success)
        {
            return pathResult;
        }
        bindings[3] = new ActionSuggestedBinding { Action = _leftYAction, Binding = leftYPath };

        pathResult = StringToPath(_instance, "/user/hand/right/input/a/click", out var rightAPath);
        if (pathResult != Result.Success)
        {
            return pathResult;
        }
        bindings[4] = new ActionSuggestedBinding { Action = _rightAAction, Binding = rightAPath };

        pathResult = StringToPath(_instance, "/user/hand/right/input/b/click", out var rightBPath);
        if (pathResult != Result.Success)
        {
            return pathResult;
        }
        bindings[5] = new ActionSuggestedBinding { Action = _rightBAction, Binding = rightBPath };

        var suggestionResult = SuggestBindingForProfile(
            _instance,
            "/interaction_profiles/oculus/touch_controller",
            bindings
        );
        if (suggestionResult != Result.Success)
        {
            return suggestionResult;
        }

        var attachInfo = new SessionActionSetsAttachInfo
        {
            Type = StructureType.SessionActionSetsAttachInfo,
            CountActionSets = 1,
        };
        var actionSet = _actionSet;
        attachInfo.ActionSets = &actionSet;
        return _xr.AttachSessionActionSets(_session, ref attachInfo);
    }

    private Result SyncActions()
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var activeActionSet = new ActiveActionSet
        {
            ActionSet = _actionSet,
            SubactionPath = XR.NullPath,
        };
        var syncInfo = new ActionsSyncInfo
        {
            Type = StructureType.ActionsSyncInfo,
            CountActiveActionSets = 1,
            ActiveActionSets = &activeActionSet,
        };
        return _xr.SyncAction(_session, ref syncInfo);
    }

    private unsafe Result CreateAction(
        ActionSet actionSet,
        string actionName,
        string localizedActionName,
        ActionType actionType,
        ref XrAction action
    )
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var actionCreateInfo = new ActionCreateInfo
        {
            Type = StructureType.ActionCreateInfo,
            ActionType = actionType,
        };
        var internalActionName = actionCreateInfo.ActionName;
        WriteFixedUtf8(internalActionName, (int)XR.MaxActionNameSize, actionName);
        var internalLocalizedActionName = actionCreateInfo.LocalizedActionName;
        WriteFixedUtf8(
            internalLocalizedActionName,
            (int)XR.MaxLocalizedActionNameSize,
            localizedActionName
        );
        return _xr.CreateAction(actionSet, ref actionCreateInfo, ref action);
    }

    private unsafe Result SuggestBindingForProfile(
        Instance instance,
        string interactionProfilePath,
        ActionSuggestedBinding[] bindings
    )
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var interactionProfilePathResult = StringToPath(
            instance,
            interactionProfilePath,
            out var interactionProfile
        );
        if (interactionProfilePathResult != Result.Success)
        {
            return interactionProfilePathResult;
        }

        fixed (ActionSuggestedBinding* suggestedBindings = bindings)
        {
            var suggestedBinding = new InteractionProfileSuggestedBinding
            {
                Type = StructureType.InteractionProfileSuggestedBinding,
                InteractionProfile = interactionProfile,
                CountSuggestedBindings = (uint)bindings.Length,
                SuggestedBindings = suggestedBindings,
            };
            return _xr.SuggestInteractionProfileBinding(instance, ref suggestedBinding);
        }
    }
}
