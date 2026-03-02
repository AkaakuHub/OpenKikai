using System.Runtime.InteropServices;
using LLMeta.App.Models;
using LLMeta.App.Utils;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace LLMeta.App.Services;

public sealed class VideoH264DecodeService : IDisposable
{
    private enum DecoderOutputPixelFormat
    {
        Unknown = 0,
        Bgra32 = 1,
        Nv12 = 2,
    }

    private readonly AppLogger _logger;

    private IMFTransform? _decoder;
    private bool _isStarted;
    private bool _outputTypeSet;
    private int _outputWidth;
    private int _outputHeight;
    private DecoderOutputPixelFormat _outputPixelFormat = DecoderOutputPixelFormat.Unknown;
    private long _sampleTime100Ns;
    private DecodedVideoFrame? _latestFrame;
    private readonly object _frameLock = new();
    private bool _loggedFirstDecodedFrame;

    public VideoH264DecodeService(AppLogger logger)
    {
        _logger = logger;
    }

    public bool TryGetLatestFrame(out DecodedVideoFrame frame)
    {
        lock (_frameLock)
        {
            if (_latestFrame is null)
            {
                frame = default;
                return false;
            }

            frame = _latestFrame.Value;
            _latestFrame = null;
            return true;
        }
    }

    public string Decode(VideoFramePacket packet)
    {
        try
        {
            EnsureStarted();
            if (_decoder is null)
            {
                return "decoder unavailable";
            }

            using var sample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(packet.Payload.Length);

            buffer.Lock(out var pBuffer, out _, out _);
            try
            {
                Marshal.Copy(packet.Payload, 0, pBuffer, packet.Payload.Length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = packet.Payload.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = _sampleTime100Ns;
            sample.SampleDuration = 10_000_000 / 90;
            _sampleTime100Ns += sample.SampleDuration;

            var inputStatus = _decoder.GetInputStatus(0);
            if ((inputStatus & (int)InputStatusFlags.InputStatusAcceptData) == 0)
            {
                _ = DrainOutputs(packet, out _);
            }

            _decoder.ProcessInput(0, sample, 0);
            var drained = DrainOutputs(packet, out var producedFrame);
            if (!drained)
            {
                return "need more input";
            }

            return producedFrame ? "decoded frame" : "drained no frame";
        }
        catch (Exception ex)
        {
            _logger.Error("Video decode failed.", ex);
            ResetDecoderAfterFailure();
            return "decode failed: " + ex.Message;
        }
    }

    public void Dispose()
    {
        _decoder?.Dispose();
        _decoder = null;
        if (_isStarted)
        {
            NativeMediaFoundation.MFShutdownChecked();
            _isStarted = false;
        }
    }

    private void EnsureStarted()
    {
        if (_decoder is not null)
        {
            return;
        }

        if (!_isStarted)
        {
            NativeMediaFoundation.MFStartupFull();
            _isStarted = true;
            _logger.Info("Media Foundation started: full startup.");
        }

        var decoderTransform = CreateDecoderTransform();
        if (decoderTransform is null)
        {
            throw new InvalidOperationException(
                "No H.264 decoder MFT was found (hardware and software)."
            );
        }
        _decoder = decoderTransform;

        using var decoderInputType = MediaFactory.MFCreateMediaType();
        decoderInputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        decoderInputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        _decoder.SetInputType(0, decoderInputType, (int)SetTypeFlags.None);

        _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        _logger.Info("Video decoder started: H.264 MFT initialized.");
    }

    private bool DrainOutputs(VideoFramePacket packet, out bool producedFrame)
    {
        producedFrame = false;
        if (_decoder is null)
        {
            return false;
        }

        while (true)
        {
            if (!_outputTypeSet)
            {
                if (!TrySetOutputType())
                {
                    return false;
                }
            }

            var streamInfo = _decoder.GetOutputStreamInfo(0);
            using var outputSample = CreateOutputSample(streamInfo);
            var outputBuffer = new OutputDataBuffer
            {
                StreamID = 0,
                Sample = outputSample,
                Status = 0,
                Events = null!,
            };

            var result = _decoder.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref outputBuffer,
                out _
            );
            if (result == ResultCode.TransformNeedMoreInput)
            {
                return true;
            }

            if (result == ResultCode.TransformStreamChange)
            {
                _outputTypeSet = false;
                continue;
            }

            if (result.Failure)
            {
                throw new InvalidOperationException("ProcessOutput failed: " + result);
            }

            if (outputBuffer.Sample is null)
            {
                return true;
            }

            var contiguous = outputBuffer.Sample.ConvertToContiguousBuffer();
            if (contiguous is null)
            {
                return true;
            }

            using (contiguous)
            {
                contiguous.Lock(out var pData, out _, out var currentLength);
                try
                {
                    var bytes = new byte[currentLength];
                    Marshal.Copy(pData, bytes, 0, currentLength);
                    var bgra = _outputPixelFormat switch
                    {
                        DecoderOutputPixelFormat.Bgra32 => ConvertRgb32ToBgra(
                            bytes,
                            _outputWidth,
                            _outputHeight
                        ),
                        DecoderOutputPixelFormat.Nv12 => ConvertNv12ToBgra(
                            bytes,
                            _outputWidth,
                            _outputHeight
                        ),
                        _ => throw new InvalidOperationException(
                            "Unsupported output pixel format."
                        ),
                    };
                    var frame = new DecodedVideoFrame(
                        packet.Sequence,
                        packet.TimestampUnixMs,
                        _outputWidth,
                        _outputHeight,
                        bgra
                    );
                    lock (_frameLock)
                    {
                        _latestFrame = frame;
                    }
                    if (!_loggedFirstDecodedFrame)
                    {
                        _loggedFirstDecodedFrame = true;
                        _logger.Info(
                            "Video decoder first frame produced: "
                                + $"seq={packet.Sequence} width={_outputWidth} height={_outputHeight}"
                        );
                    }
                    producedFrame = true;
                }
                finally
                {
                    contiguous.Unlock();
                }
            }
        }
    }

    private bool TrySetOutputType()
    {
        if (_decoder is null)
        {
            return false;
        }

        var firstNv12Index = -1;
        for (var index = 0; ; index++)
        {
            IMFMediaType? mediaType = null;
            try
            {
                mediaType = _decoder.GetOutputAvailableType(0, index);
            }
            catch
            {
                break;
            }

            using (mediaType)
            {
                var subtype = mediaType.GetGUID(MediaTypeAttributeKeys.Subtype);
                if (subtype == VideoFormatGuids.Rgb32)
                {
                    return ApplyOutputType(mediaType, subtype);
                }

                if (subtype == VideoFormatGuids.NV12 && firstNv12Index < 0)
                {
                    firstNv12Index = index;
                }
            }
        }

        if (firstNv12Index < 0)
        {
            return false;
        }

        using var nv12Type = _decoder.GetOutputAvailableType(0, firstNv12Index);
        var nv12Subtype = nv12Type.GetGUID(MediaTypeAttributeKeys.Subtype);
        return ApplyOutputType(nv12Type, nv12Subtype);
    }

    private bool ApplyOutputType(IMFMediaType mediaType, Guid subtype)
    {
        if (_decoder is null)
        {
            return false;
        }

        _decoder.SetOutputType(0, mediaType, (int)SetTypeFlags.None);
        var frameSize = mediaType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        _outputWidth = (int)(frameSize >> 32);
        _outputHeight = (int)(frameSize & 0xFFFFFFFF);
        _outputPixelFormat =
            subtype == VideoFormatGuids.Rgb32
                ? DecoderOutputPixelFormat.Bgra32
                : DecoderOutputPixelFormat.Nv12;
        _outputTypeSet = _outputWidth > 0 && _outputHeight > 0;
        _logger.Info(
            "Video decoder output format ready: "
                + $"width={_outputWidth} height={_outputHeight} subtype={subtype}"
        );
        return _outputTypeSet;
    }

    private IMFTransform? CreateDecoderTransform()
    {
        var h264InputType = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };
        var h264EsInputType = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264Es,
        };
        RegisterTypeInfo?[] inputCandidates = [h264InputType, h264EsInputType];
        RegisterTypeInfo?[] outputCandidates =
        [
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video },
            null,
        ];

        var hardwarePreferredFlags = (uint)(
            EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter
        );
        foreach (var inputCandidate in inputCandidates)
        {
            foreach (var outputCandidate in outputCandidates)
            {
                var decoder = EnumerateAndActivateDecoder(
                    hardwarePreferredFlags,
                    inputCandidate,
                    outputCandidate
                );
                if (decoder is not null)
                {
                    _logger.Info(
                        "Video decoder selected: hardware-preferred MFT."
                            + $" inputSubtype={DescribeInputSubtype(inputCandidate)}"
                            + $" outputFilter={DescribeOutputFilter(outputCandidate)}"
                    );
                    return decoder;
                }
            }
        }

        var broadFlags = (uint)EnumFlag.EnumFlagAll;
        foreach (var inputCandidate in inputCandidates)
        {
            foreach (var outputCandidate in outputCandidates)
            {
                var decoder = EnumerateAndActivateDecoder(
                    broadFlags,
                    inputCandidate,
                    outputCandidate
                );
                if (decoder is not null)
                {
                    _logger.Info(
                        "Video decoder selected: broad-enumeration MFT."
                            + $" inputSubtype={DescribeInputSubtype(inputCandidate)}"
                            + $" outputFilter={DescribeOutputFilter(outputCandidate)}"
                    );
                    return decoder;
                }
            }
        }

        return null;
    }

    private static IMFTransform? EnumerateAndActivateDecoder(
        uint flags,
        RegisterTypeInfo? inputType,
        RegisterTypeInfo? outputType
    )
    {
        using var activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            flags,
            inputType,
            outputType
        );
        var activate = activates.FirstOrDefault();
        if (activate is null)
        {
            return null;
        }

        return activate.ActivateObject<IMFTransform>();
    }

    private static string DescribeInputSubtype(RegisterTypeInfo? inputType)
    {
        if (inputType is null)
        {
            return "null";
        }

        if (inputType.Value.GuidSubtype == VideoFormatGuids.H264Es)
        {
            return "H264Es";
        }

        if (inputType.Value.GuidSubtype == VideoFormatGuids.H264)
        {
            return "H264";
        }

        return inputType.Value.GuidSubtype.ToString();
    }

    private static string DescribeOutputFilter(RegisterTypeInfo? outputType)
    {
        return outputType is null ? "null" : "video-major";
    }

    private static class NativeMediaFoundation
    {
        private const int MfVersion = 0x00020070;

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        public static void MFStartupFull()
        {
            var hr = MFStartup(MfVersion, 0);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        public static void MFShutdownChecked()
        {
            var hr = MFShutdown();
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
    }

    private static IMFSample CreateOutputSample(OutputStreamInfo streamInfo)
    {
        if ((streamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0)
        {
            return null!;
        }

        var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(streamInfo.Size);
        sample.AddBuffer(buffer);
        return sample;
    }

    private void ResetDecoderAfterFailure()
    {
        _decoder?.Dispose();
        _decoder = null;
        _outputTypeSet = false;
        _outputWidth = 0;
        _outputHeight = 0;
        _outputPixelFormat = DecoderOutputPixelFormat.Unknown;
        _sampleTime100Ns = 0;
        _loggedFirstDecodedFrame = false;
    }

    private static byte[] ConvertRgb32ToBgra(byte[] rgb32, int width, int height)
    {
        var requiredLength = checked(width * height * 4);
        if (rgb32.Length < requiredLength)
        {
            throw new InvalidOperationException(
                $"RGB32 buffer too small. got={rgb32.Length} required={requiredLength}"
            );
        }

        if (rgb32.Length == requiredLength)
        {
            return rgb32;
        }

        var sourceStride = rgb32.Length / height;
        if (sourceStride < width * 4)
        {
            throw new InvalidOperationException(
                $"RGB32 stride too small. stride={sourceStride} width={width}"
            );
        }

        var trimmed = new byte[requiredLength];
        for (var y = 0; y < height; y++)
        {
            var sourceOffset = y * sourceStride;
            var targetOffset = y * width * 4;
            Buffer.BlockCopy(rgb32, sourceOffset, trimmed, targetOffset, width * 4);
        }

        return trimmed;
    }

    private static byte[] ConvertNv12ToBgra(byte[] nv12, int width, int height)
    {
        var minimumRequired = checked(width * height * 3 / 2);
        if (nv12.Length < minimumRequired)
        {
            throw new InvalidOperationException(
                $"NV12 buffer too small. got={nv12.Length} required={minimumRequired}"
            );
        }

        var sourceStride = width;
        var sourceHeight = height;
        var guessedHeight = (nv12.Length * 2) / (width * 3);
        if (guessedHeight >= height && (width * guessedHeight * 3) / 2 == nv12.Length)
        {
            sourceHeight = guessedHeight;
        }

        var yPlaneSize = sourceStride * sourceHeight;
        var uvPlaneStart = yPlaneSize;
        var uvStride = sourceStride;
        if (uvPlaneStart + (uvStride * (sourceHeight / 2)) > nv12.Length)
        {
            throw new InvalidOperationException(
                $"NV12 layout invalid. length={nv12.Length} stride={sourceStride} sourceHeight={sourceHeight}"
            );
        }

        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var uvRow = (y / 2) * uvStride;
            for (var x = 0; x < width; x++)
            {
                var yValue = nv12[y * sourceStride + x];
                var uvIndex = uvPlaneStart + uvRow + (x & ~1);
                var uValue = nv12[uvIndex];
                var vValue = nv12[uvIndex + 1];

                var c = yValue - 16;
                var d = uValue - 128;
                var e = vValue - 128;

                var r = ClampToByte((298 * c + 409 * e + 128) >> 8);
                var g = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
                var b = ClampToByte((298 * c + 516 * d + 128) >> 8);

                var dst = (y * width + x) * 4;
                bgra[dst] = (byte)b;
                bgra[dst + 1] = (byte)g;
                bgra[dst + 2] = (byte)r;
                bgra[dst + 3] = 255;
            }
        }

        return bgra;
    }

    private static int ClampToByte(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > 255)
        {
            return 255;
        }

        return value;
    }
}
