using NDesk.WebRtcSpike.Capture;
using NDesk.WebRtcSpike.Codec;
using NDesk.WebRtcSpike.Metrics;
using NDesk.WebRtcSpike.Transport;

// SPIKE-017 — PoC descartável. Ver docs/spikes/SPIKE-017-ndesk-webrtc-captura-win10.md.
// Uso: dotnet run -- [segundos=10] [monitor=0]

int durationSeconds = args.Length > 0 && int.TryParse(args[0], out var d) ? d : 10;
int monitorIndex = args.Length > 1 && int.TryParse(args[1], out var m) ? m : 0;

Console.WriteLine("SPIKE-017 — PoC: DXGI (Vortice.Windows) + libdatachannel (P/Invoke), loopback local.");
Console.WriteLine($"Duração alvo: {durationSeconds}s | Monitor: {monitorIndex}");
Console.WriteLine();

DxgiScreenCapture capture;
try
{
    capture = new DxgiScreenCapture(monitorIndex);
}
catch (Exception ex)
{
    Console.WriteLine("PENDENTE — não foi possível inicializar a captura DXGI neste ambiente.");
    Console.WriteLine($"Motivo: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Causas prováveis: sessão sem GPU/adaptador de vídeo real (ex.: RDP/headless sem");
    Console.WriteLine("driver de exibição), ou WDDM indisponível na sessão atual. Rode este PoC numa sessão");
    Console.WriteLine("de console local (não RDP) em Windows 10/11 com GPU real para obter as métricas.");
    return 1;
}

Console.WriteLine($"Captura DXGI inicializada: {capture.Width}x{capture.Height}");

using var session = new LoopbackDataChannelSession();
var tracker = new LatencyFpsTracker();
var codec = new PlaceholderFrameCodec();

session.FrameReceivedByViewer += (capturedAtUtc, payload) =>
{
    tracker.RecordFrame(capturedAtUtc, DateTime.UtcNow, payload.Length);
};

Console.WriteLine("Negociando PeerConnections (loopback, sem broker/signaling)...");
try
{
    await session.ConnectAsync(TimeSpan.FromSeconds(10));
}
catch (Exception ex)
{
    Console.WriteLine("PENDENTE — DataChannel libdatachannel não abriu.");
    Console.WriteLine($"Motivo: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Causas prováveis: datachannel.dll ausente do output (verificar restore do pacote");
    Console.WriteLine("DataChannelDotnet), ou firewall/AV bloqueando ICE UDP mesmo em loopback (127.0.0.1).");
    capture.Dispose();
    return 1;
}

Console.WriteLine("DataChannel aberto. Capturando e enviando frames...");
Console.WriteLine();

var deadline = DateTime.UtcNow.AddSeconds(durationSeconds);
int framesSent = 0;
while (DateTime.UtcNow < deadline)
{
    var frame = capture.TryCaptureNextFrame(timeoutMs: 500);
    if (frame is null)
        continue; // tela estática — DXGI não entrega frame sem mudança (comportamento esperado, não erro).

    var encoded = codec.EncodeDelta(frame.Bgra);
    session.SendFrame(encoded, frame.CapturedAtUtc);
    framesSent++;
}

// Dá tempo do SCTP entregar os últimos frames em voo antes de medir.
await Task.Delay(500);

Console.WriteLine($"Frames capturados/enviados: {framesSent}");
tracker.PrintSummary();

capture.Dispose();
return 0;
