namespace LLMeta.App.Models;

public readonly record struct OpenXrControllerState(
    bool IsInitialized,
    string Status,
    float LeftStickX,
    float LeftStickY,
    float RightStickX,
    float RightStickY,
    bool LeftXPressed,
    bool LeftYPressed,
    bool RightAPressed,
    bool RightBPressed
);
