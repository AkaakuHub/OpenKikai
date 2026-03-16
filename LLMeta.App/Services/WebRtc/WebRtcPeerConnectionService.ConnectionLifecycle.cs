using LLMeta.App.Models;
using SIPSorcery.Net;

namespace LLMeta.App.Services;

public sealed partial class WebRtcPeerConnectionService
{
    private void EnsurePeerConnection()
    {
        if (_peerConnection is not null)
        {
            return;
        }

        lock (_stateLock)
        {
            _candidateMid = "0";
            _candidateMLineIndex = 0;
        }

        var config = new RTCConfiguration
        {
            X_ICEIncludeAllInterfaceAddresses = true,
            iceServers = [new RTCIceServer { urls = "stun:stun.l.google.com:19302" }],
        };
        _peerConnection = new RTCPeerConnection(config);
        _peerConnection.onicecandidate += candidate =>
        {
            if (candidate is null)
            {
                return;
            }
            _logger.Info(
                $"WebRTC local ICE candidate: mid={candidate.sdpMid} mline={candidate.sdpMLineIndex} candidate={candidate.candidate}"
            );
            var normalizedCandidate = NormalizeLocalIceCandidate(candidate.candidate);
            string outboundMid;
            ushort outboundMLineIndex;
            if (!string.IsNullOrWhiteSpace(candidate.sdpMid))
            {
                outboundMid = candidate.sdpMid;
                outboundMLineIndex = candidate.sdpMLineIndex;
            }
            else
            {
                lock (_stateLock)
                {
                    outboundMid = _candidateMid;
                    outboundMLineIndex = _candidateMLineIndex;
                }
            }
            OutboundSignalingMessage?.Invoke(
                new WebRtcSignalingMessage
                {
                    Type = "ice-candidate",
                    Candidate = normalizedCandidate,
                    SdpMid = outboundMid,
                    SdpMLineIndex = outboundMLineIndex,
                }
            );
        };
        _peerConnection.onconnectionstatechange += state =>
        {
            _logger.Info($"WebRTC peer connection state: {state}");
        };
        _peerConnection.oniceconnectionstatechange += state =>
        {
            _logger.Info($"WebRTC ICE connection state: {state}");
        };
        _peerConnection.ondatachannel += dataChannel =>
        {
            _logger.Info($"WebRTC data channel opened: {dataChannel.label}");
            if (!string.Equals(dataChannel.label, InputDataChannelLabel, StringComparison.Ordinal))
            {
                return;
            }

            SetInputDataChannel(dataChannel);
            dataChannel.onmessage += (_, _, payload) =>
            {
                if (payload is not null && payload.Length > 0)
                {
                    _logger.Info(
                        $"WebRTC data message: channel={dataChannel.label} size={payload.Length}"
                    );
                }
            };
            dataChannel.onopen += () =>
            {
                lock (_stateLock)
                {
                    _inputChannelStatusText = $"Input channel: open ({dataChannel.label})";
                }
            };
            dataChannel.onclose += () =>
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_inputDataChannel, dataChannel))
                    {
                        _inputDataChannel = null;
                        _inputChannelStatusText = "Input channel: closed, waiting reconnect";
                    }
                }
            };
        };
    }

    private void ClosePeerConnection()
    {
        if (_peerConnection is null)
        {
            return;
        }

        _peerConnection.close();
        _peerConnection.Dispose();
        _peerConnection = null;
        SetInputDataChannel(null);
    }
}
