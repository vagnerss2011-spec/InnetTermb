using System.IO.Compression;

namespace NDesk.WebRtcSpike.Codec;

/// <summary>
/// PLACEHOLDER deliberado — NÃO é H.264/VP8. Faz diff de frame anterior (dirty-region grosseiro,
/// por linha) + GZip, só para o pipeline capture→encode→transporte ter algo mensurável fim a
/// fim sem depender de um encoder de vídeo real.
///
/// Por quê: a escolha de codec (H.264/OpenH264 vs VP8) já é um item PRÓPRIO e separado em
/// docs/22-ndesk-performance-legacy-windows.md ("Spikes obrigatórios" #3) e docs/15
/// (SPIKE-010 item "Codec"), fora do escopo deste SPIKE-017 (que decide biblioteca de
/// transporte + captura, não codec). Ver docs/spikes/SPIKE-017-ndesk-webrtc-captura-win10.md
/// §Pendências para o que falta: integrar OpenH264 (BSD-2, C API, prebuilt DLL da Cisco) ou
/// Media Foundation H.264 encoder antes de qualquer medição de bitrate real.
/// </summary>
internal sealed class PlaceholderFrameCodec
{
    private byte[]? _previousFrame;

    public byte[] EncodeDelta(byte[] currentFrame)
    {
        byte[] toCompress;
        if (_previousFrame is not null && _previousFrame.Length == currentFrame.Length)
        {
            toCompress = new byte[currentFrame.Length];
            for (int i = 0; i < currentFrame.Length; i++)
                toCompress[i] = (byte)(currentFrame[i] ^ _previousFrame[i]);
        }
        else
        {
            toCompress = currentFrame;
        }

        _previousFrame = currentFrame;

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(toCompress, 0, toCompress.Length);
        return output.ToArray();
    }
}
