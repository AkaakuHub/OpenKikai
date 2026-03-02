namespace LLMeta.App.Models;

public readonly record struct DecodedVideoFrame(
    uint Sequence,
    ulong TimestampUnixMs,
    int Width,
    int Height,
    byte[] BgraPixels
);
