using System.Net;
using System.Text.RegularExpressions;
using LLMeta.App.Models;
using LLMeta.App.Utils;
using SIPSorcery.Net;

namespace LLMeta.App.Services;

public sealed partial class WebRtcPeerConnectionService : IDisposable
{
    private const string InputDataChannelLabel = "input-state";
    private static readonly TimeSpan InputTickInterval = TimeSpan.FromSeconds(1.0 / 90.0);
    private static readonly Regex CandidateIpRegex = new(
        @"^(candidate:\S+\s+\d+\s+(?:udp|tcp)\s+\d+\s+)(\S+)(\s+\d+\s+typ\s+\S+.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private readonly AppLogger _logger;
    private readonly object _stateLock = new();
    private RTCPeerConnection? _peerConnection;
    private string _candidateMid = "0";
    private ushort _candidateMLineIndex;
    private RTCDataChannel? _inputDataChannel;
    private OpenXrControllerState _latestInputState;
    private bool _isKeyboardDebugMode;
    private string _inputChannelStatusText = "Input channel: waiting data channel";
    private CancellationTokenSource? _inputSendCts;
    private Task? _inputSendTask;

    public WebRtcPeerConnectionService(AppLogger logger)
    {
        _logger = logger;
        _latestInputState = default;
        _inputSendCts = new CancellationTokenSource();
        _inputSendTask = Task.Run(() => InputSendLoopAsync(_inputSendCts.Token));
    }

    public event Action<WebRtcSignalingMessage>? OutboundSignalingMessage;

    public Task HandleSignalingMessageAsync(WebRtcSignalingMessage message)
    {
        if (message.Type == "offer")
        {
            HandleOffer(message);
            return Task.CompletedTask;
        }

        if (message.Type == "ice-candidate")
        {
            HandleRemoteIceCandidate(message);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        var inputSendCts = _inputSendCts;
        if (inputSendCts is not null)
        {
            _inputSendCts = null;
            try
            {
                inputSendCts.Cancel();
            }
            catch { }
        }

        try
        {
            _inputSendTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        finally
        {
            _inputSendTask = null;
            inputSendCts?.Dispose();
        }

        lock (_stateLock)
        {
            _candidateMid = "0";
            _candidateMLineIndex = 0;
            _inputDataChannel = null;
            _inputChannelStatusText = "Input channel: stopped";
        }

        ClosePeerConnection();
    }

    private void HandleOffer(WebRtcSignalingMessage message)
    {
        var sdp = message.Sdp;
        if (string.IsNullOrWhiteSpace(sdp))
        {
            _logger.Info("WebRTC offer ignored: empty SDP.");
            return;
        }

        ClosePeerConnection();
        EnsurePeerConnection();
        if (_peerConnection is null)
        {
            return;
        }

        var remote = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp };
        SetCandidateRoutingFromOffer(sdp);
        _logger.Info($"WebRTC offer SDP summary: {SummarizeSdp(sdp)}");
        var setRemoteResult = _peerConnection.setRemoteDescription(remote);
        if (setRemoteResult != SetDescriptionResultEnum.OK)
        {
            _logger.Info($"WebRTC setRemoteDescription failed: {setRemoteResult}");
            return;
        }

        var answer = _peerConnection.createAnswer(null);
        if (string.IsNullOrWhiteSpace(answer.sdp))
        {
            var generatedAnswer = _peerConnection.CreateAnswer(IPAddress.Loopback);
            var generatedAnswerSdp = generatedAnswer?.ToString();
            if (!string.IsNullOrWhiteSpace(generatedAnswerSdp))
            {
                answer.sdp = generatedAnswerSdp;
            }
        }

        if (string.IsNullOrWhiteSpace(answer.sdp))
        {
            _logger.Info("WebRTC answer generation failed: empty SDP.");
            return;
        }

        _peerConnection.setLocalDescription(answer);
        _logger.Info($"WebRTC answer SDP summary: {SummarizeSdp(answer.sdp)}");
        OutboundSignalingMessage?.Invoke(
            new WebRtcSignalingMessage { Type = "answer", Sdp = answer.sdp }
        );
    }
}
