using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static NDesk.WebRtcSpike.Transport.LibDataChannelNative;

namespace NDesk.WebRtcSpike.Transport;

/// <summary>
/// Duas PeerConnections libdatachannel no mesmo processo ("agente" e "viewer"), com troca de
/// SDP/candidatos ICE feita diretamente em memória (sem broker/signaling — é o que "loopback"
/// significa aqui). Mede o pipeline real de negociação + DataChannel/SCTP do libdatachannel,
/// não apenas um socket TCP cru. Ver SPIKE-017 §Transporte para a ressalva de escopo: isto
/// prova que o P/Invoke funciona e mede latência de DataChannel local; não testa NAT/TURN
/// real (isso é o SPIKE-012, já mapeado em docs/15).
///
/// Fragmenta cada frame em chunks: a negociação DCEP do libdatachannel travou o max-message-size
/// efetivo em 256KiB nesta máquina mesmo configurando rtcConfiguration.maxMessageSize maior — não
/// investigamos further (fora de escopo deste spike de transporte+captura); qualquer implementação
/// real também precisaria fragmentar frames grandes, então isto não é um desvio artificial do PoC.
/// </summary>
internal sealed class LoopbackDataChannelSession : IDisposable
{
    private const string AgentTag = "agent";
    private const string ViewerTag = "viewer";
    private const int ChunkPayloadSize = 200_000;
    private const int ChunkHeaderSize = 8 + 4 + 2 + 2 + 4; // ticks + frameId + chunkIndex + chunkCount + chunkLen

    private int _pcAgent = -1;
    private int _pcViewer = -1;
    private int _dcAgent = -1;
    private int _dcViewer = -1;
    private int _nextFrameId;

    private readonly Dictionary<int, (byte[] Buffer, int Received, int ChunkCount, long CapturedAtTicks)> _reassembly = new();

    private readonly TaskCompletionSource _agentChannelOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _viewerChannelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<DateTime, byte[]>? FrameReceivedByViewer;

    // Referências mantidas vivas explicitamente: delegates passados a P/Invoke não podem ser
    // coletados pelo GC enquanto a lib nativa ainda pode invocá-los.
    private readonly List<Delegate> _pinnedCallbacks = new();

    public async Task ConnectAsync(TimeSpan timeout)
    {
        var config = default(RtcConfiguration); // sem iceServers: só candidatos host (loopback)
        config.maxMessageSize = 1 * 1024 * 1024;

        _pcAgent = rtcCreatePeerConnection(ref config);
        _pcViewer = rtcCreatePeerConnection(ref config);
        if (_pcAgent < 0 || _pcViewer < 0)
            throw new InvalidOperationException($"rtcCreatePeerConnection falhou (agent={_pcAgent}, viewer={_pcViewer}). datachannel.dll presente no output?");

        WireCallbacks(_pcAgent, isAgent: true);
        WireCallbacks(_pcViewer, isAgent: false);

        _dcAgent = rtcCreateDataChannel(_pcAgent, "frames");
        if (_dcAgent < 0)
            throw new InvalidOperationException($"rtcCreateDataChannel falhou ({_dcAgent}).");

        WireDataChannelCallbacks(_dcAgent, AgentTag);

        await Task.WhenAny(
            Task.WhenAll(_agentChannelOpen.Task, _viewerChannelReady.Task),
            Task.Delay(timeout));

        if (!_agentChannelOpen.Task.IsCompletedSuccessfully || !_viewerChannelReady.Task.IsCompletedSuccessfully)
            throw new TimeoutException("DataChannel loopback não abriu dentro do timeout — ver README (ICE/UDP local pode estar bloqueado por firewall/AV).");
    }

    public void SendFrame(byte[] encodedFrame, DateTime capturedAtUtc)
    {
        int frameId = _nextFrameId++;
        int chunkCount = (int)Math.Ceiling(encodedFrame.Length / (double)ChunkPayloadSize);
        long ticks = capturedAtUtc.Ticks;

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            int offset = chunkIndex * ChunkPayloadSize;
            int chunkLen = Math.Min(ChunkPayloadSize, encodedFrame.Length - offset);

            var packet = new byte[ChunkHeaderSize + chunkLen];
            BitConverter.GetBytes(ticks).CopyTo(packet, 0);
            BitConverter.GetBytes(frameId).CopyTo(packet, 8);
            BitConverter.GetBytes((ushort)chunkIndex).CopyTo(packet, 12);
            BitConverter.GetBytes((ushort)chunkCount).CopyTo(packet, 14);
            BitConverter.GetBytes(chunkLen).CopyTo(packet, 16);
            Buffer.BlockCopy(encodedFrame, offset, packet, ChunkHeaderSize, chunkLen);

            int rc = rtcSendMessage(_dcAgent, packet, packet.Length);
            if (rc < 0)
                throw new InvalidOperationException($"rtcSendMessage falhou ({rc}) — chunk {chunkIndex}/{chunkCount}, {packet.Length} bytes.");
        }
    }

    private void OnChunkReceived(byte[] packet)
    {
        long ticks = BitConverter.ToInt64(packet, 0);
        int frameId = BitConverter.ToInt32(packet, 8);
        ushort chunkIndex = BitConverter.ToUInt16(packet, 12);
        ushort chunkCount = BitConverter.ToUInt16(packet, 14);
        int chunkLen = BitConverter.ToInt32(packet, 16);

        if (!_reassembly.TryGetValue(frameId, out var entry))
        {
            // Tamanho total desconhecido a priori (chunks podem chegar fora de ordem sobre SCTP
            // não-ordenado); superestimamos com chunkCount*ChunkPayloadSize e cortamos no fim.
            entry = (new byte[chunkCount * ChunkPayloadSize], 0, chunkCount, ticks);
        }

        Buffer.BlockCopy(packet, ChunkHeaderSize, entry.Buffer, chunkIndex * ChunkPayloadSize, chunkLen);
        entry.Received++;
        _reassembly[frameId] = entry;

        if (entry.Received == entry.ChunkCount)
        {
            _reassembly.Remove(frameId);
            var capturedAtUtc = new DateTime(entry.CapturedAtTicks, DateTimeKind.Utc);
            FrameReceivedByViewer?.Invoke(capturedAtUtc, entry.Buffer);
        }
    }

    private void WireCallbacks(int pc, bool isAgent)
    {
        DescriptionCallback onLocalDescription = (sourcePc, sdp, type, _) =>
        {
            int targetPc = isAgent ? _pcViewer : _pcAgent;
            rtcSetRemoteDescription(targetPc, sdp, type);
        };

        CandidateCallback onLocalCandidate = (sourcePc, cand, mid, _) =>
        {
            int targetPc = isAgent ? _pcViewer : _pcAgent;
            rtcAddRemoteCandidate(targetPc, cand, mid);
        };

        DataChannelCallback onDataChannel = (sourcePc, dc, _) =>
        {
            // Só o lado viewer recebe este callback (o agente criou o canal explicitamente).
            _dcViewer = dc;
            WireDataChannelCallbacks(dc, ViewerTag);
            _viewerChannelReady.TrySetResult();
        };

        _pinnedCallbacks.Add(onLocalDescription);
        _pinnedCallbacks.Add(onLocalCandidate);
        _pinnedCallbacks.Add(onDataChannel);

        rtcSetLocalDescriptionCallback(pc, onLocalDescription);
        rtcSetLocalCandidateCallback(pc, onLocalCandidate);
        rtcSetDataChannelCallback(pc, onDataChannel);
    }

    private void WireDataChannelCallbacks(int dc, string tag)
    {
        OpenCallback onOpen = (id, _) =>
        {
            if (tag == AgentTag)
                _agentChannelOpen.TrySetResult();
        };

        MessageCallback onMessage = (id, message, size, _) =>
        {
            if (size <= 0)
                return; // libdatachannel também sinaliza mensagens de texto/controle com size<0; ignoradas aqui.

            var buffer = new byte[size];
            Marshal.Copy(message, buffer, 0, size);
            OnChunkReceived(buffer);
        };

        _pinnedCallbacks.Add(onOpen);
        _pinnedCallbacks.Add(onMessage);

        rtcSetOpenCallback(dc, onOpen);
        rtcSetMessageCallback(dc, onMessage);
    }

    public void Dispose()
    {
        if (_dcAgent >= 0) rtcDeleteDataChannel(_dcAgent);
        if (_dcViewer >= 0) rtcDeleteDataChannel(_dcViewer);
        if (_pcAgent >= 0) { rtcClosePeerConnection(_pcAgent); rtcDeletePeerConnection(_pcAgent); }
        if (_pcViewer >= 0) { rtcClosePeerConnection(_pcViewer); rtcDeletePeerConnection(_pcViewer); }
    }
}
