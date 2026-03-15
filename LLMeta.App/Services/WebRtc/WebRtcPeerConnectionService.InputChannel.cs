using System.Buffers.Binary;
using LLMeta.App.Models;
using SIPSorcery.Net;

namespace LLMeta.App.Services;

public sealed partial class WebRtcPeerConnectionService
{
    private const int InputPayloadSize = 108;

    public void UpdateLatestInputState(OpenXrControllerState state, bool isKeyboardDebugMode)
    {
        lock (_stateLock)
        {
            _latestInputState = state;
            _isKeyboardDebugMode = isKeyboardDebugMode;
        }
    }

    public string GetInputChannelStatusText()
    {
        lock (_stateLock)
        {
            return _inputChannelStatusText;
        }
    }

    private void SetInputDataChannel(RTCDataChannel? channel)
    {
        lock (_stateLock)
        {
            _inputDataChannel = channel;
            _inputChannelStatusText = channel is null
                ? "Input channel: waiting data channel"
                : $"Input channel: data channel ready ({channel.label})";
        }
    }

    private async Task InputSendLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(InputTickInterval);
        var payload = new byte[InputPayloadSize];

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            RTCDataChannel? channel;
            InputFrame frame;
            lock (_stateLock)
            {
                channel = _inputDataChannel;
                frame = InputFrame.FromState(_latestInputState, _isKeyboardDebugMode);
            }

            if (channel is null || channel.readyState != RTCDataChannelState.open)
            {
                continue;
            }

            BuildInputPayload(payload, frame);
            try
            {
                channel.send(payload);
            }
            catch (Exception ex)
            {
                _logger.Error("WebRTC input data channel send failed.", ex);
                lock (_stateLock)
                {
                    _inputDataChannel = null;
                    _inputChannelStatusText = "Input channel: send error, waiting reconnect";
                }
            }
        }
    }

    private static void BuildInputPayload(byte[] destination, InputFrame frame)
    {
        var span = destination.AsSpan();
        span.Clear();

        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(0, 4), frame.LeftStickX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(4, 4), frame.LeftStickY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(8, 4), frame.RightStickX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(12, 4), frame.RightStickY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(16, 4), frame.LeftTriggerValue);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(20, 4), frame.LeftGripValue);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(24, 4), frame.RightTriggerValue);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(28, 4), frame.RightGripValue);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(32, 4), frame.OrientationX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(36, 4), frame.OrientationY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(40, 4), frame.OrientationZ);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(44, 4), frame.OrientationW);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(48, 4), frame.HmdPositionX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(52, 4), frame.HmdPositionY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(56, 4), frame.HmdPositionZ);
        BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(60, 4),
            unchecked((int)frame.ButtonsBitMask)
        );
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(64, 4), frame.Flags);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(68, 4), frame.IpdMeters);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(72, 4), frame.HmdVerticalFovDegrees);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(76, 4), frame.LeftEyeAngleLeftRadians);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(80, 4), frame.LeftEyeAngleRightRadians);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(84, 4), frame.LeftEyeAngleUpRadians);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(88, 4), frame.LeftEyeAngleDownRadians);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(92, 4), frame.RightEyeAngleLeftRadians);
        BinaryPrimitives.WriteSingleLittleEndian(
            span.Slice(96, 4),
            frame.RightEyeAngleRightRadians
        );
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(100, 4), frame.RightEyeAngleUpRadians);
        BinaryPrimitives.WriteSingleLittleEndian(
            span.Slice(104, 4),
            frame.RightEyeAngleDownRadians
        );
    }

    private readonly record struct InputFrame(
        float LeftStickX,
        float LeftStickY,
        float RightStickX,
        float RightStickY,
        float LeftTriggerValue,
        float LeftGripValue,
        float RightTriggerValue,
        float RightGripValue,
        float OrientationX,
        float OrientationY,
        float OrientationZ,
        float OrientationW,
        float HmdPositionX,
        float HmdPositionY,
        float HmdPositionZ,
        uint ButtonsBitMask,
        int Flags,
        float IpdMeters,
        float HmdVerticalFovDegrees,
        float LeftEyeAngleLeftRadians,
        float LeftEyeAngleRightRadians,
        float LeftEyeAngleUpRadians,
        float LeftEyeAngleDownRadians,
        float RightEyeAngleLeftRadians,
        float RightEyeAngleRightRadians,
        float RightEyeAngleUpRadians,
        float RightEyeAngleDownRadians
    )
    {
        private const uint ButtonA = 1 << 0;
        private const uint ButtonB = 1 << 1;
        private const uint ButtonX = 1 << 2;
        private const uint ButtonY = 1 << 3;
        private const uint ButtonLeftStickClick = 1 << 4;
        private const uint ButtonRightStickClick = 1 << 5;

        public static InputFrame FromState(OpenXrControllerState state, bool isKeyboardDebugMode)
        {
            var buttons = 0u;
            if (state.RightAPressed)
            {
                buttons |= ButtonA;
            }

            if (state.RightBPressed)
            {
                buttons |= ButtonB;
            }

            if (state.LeftXPressed)
            {
                buttons |= ButtonX;
            }

            if (state.LeftYPressed)
            {
                buttons |= ButtonY;
            }

            if (state.LeftStickClickPressed)
            {
                buttons |= ButtonLeftStickClick;
            }

            if (state.RightStickClickPressed)
            {
                buttons |= ButtonRightStickClick;
            }

            var flags = 0;
            if (isKeyboardDebugMode)
            {
                flags |= 1;
            }

            // Send raw OpenXR pose values here. The receiver owns camera-mode conversion and session baselines.
            return new InputFrame(
                state.LeftStickX,
                state.LeftStickY,
                state.RightStickX,
                state.RightStickY,
                state.LeftTriggerValue,
                state.LeftGripValue,
                state.RightTriggerValue,
                state.RightGripValue,
                state.HeadPose.OrientationX,
                state.HeadPose.OrientationY,
                state.HeadPose.OrientationZ,
                state.HeadPose.OrientationW,
                state.HeadPose.PositionX,
                state.HeadPose.PositionY,
                state.HeadPose.PositionZ,
                buttons,
                flags,
                state.IpdMeters,
                state.HmdVerticalFovDegrees,
                state.LeftEyeAngleLeftRadians,
                state.LeftEyeAngleRightRadians,
                state.LeftEyeAngleUpRadians,
                state.LeftEyeAngleDownRadians,
                state.RightEyeAngleLeftRadians,
                state.RightEyeAngleRightRadians,
                state.RightEyeAngleUpRadians,
                state.RightEyeAngleDownRadians
            );
        }
    }
}
