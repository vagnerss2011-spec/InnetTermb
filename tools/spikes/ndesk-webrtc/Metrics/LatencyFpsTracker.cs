namespace NDesk.WebRtcSpike.Metrics;

internal sealed class LatencyFpsTracker
{
    private readonly List<double> _latenciesMs = new();
    private readonly List<int> _payloadSizes = new();
    private DateTime _firstFrameAtUtc;
    private DateTime _lastFrameAtUtc;
    private int _frameCount;

    public void RecordFrame(DateTime capturedAtUtc, DateTime receivedAtUtc, int payloadBytes)
    {
        if (_frameCount == 0)
            _firstFrameAtUtc = capturedAtUtc;

        _lastFrameAtUtc = receivedAtUtc;
        _frameCount++;
        _latenciesMs.Add((receivedAtUtc - capturedAtUtc).TotalMilliseconds);
        _payloadSizes.Add(payloadBytes);
    }

    public void PrintSummary()
    {
        if (_frameCount == 0)
        {
            Console.WriteLine("Nenhum frame recebido — sem métricas.");
            return;
        }

        var elapsedSeconds = Math.Max((_lastFrameAtUtc - _firstFrameAtUtc).TotalSeconds, 0.001);
        var fps = _frameCount / elapsedSeconds;
        var sortedLatencies = _latenciesMs.OrderBy(x => x).ToList();
        var p50 = Percentile(sortedLatencies, 0.50);
        var p95 = Percentile(sortedLatencies, 0.95);
        var avgLatency = _latenciesMs.Average();
        var avgPayload = _payloadSizes.Average();
        var totalBytes = _payloadSizes.Sum();
        var avgKbps = (totalBytes * 8.0 / 1000.0) / elapsedSeconds;

        Console.WriteLine();
        Console.WriteLine("=== SPIKE-017 — resultado da medição (loopback local) ===");
        Console.WriteLine($"Frames entregues:        {_frameCount}");
        Console.WriteLine($"Duração:                 {elapsedSeconds:F2} s");
        Console.WriteLine($"FPS médio (entregue):    {fps:F2}");
        Console.WriteLine($"Latência média (ms):     {avgLatency:F2}");
        Console.WriteLine($"Latência p50 (ms):       {p50:F2}");
        Console.WriteLine($"Latência p95 (ms):       {p95:F2}");
        Console.WriteLine($"Payload médio (bytes):   {avgPayload:F0}");
        Console.WriteLine($"Bitrate médio (kbps):    {avgKbps:F1}");
        Console.WriteLine("Nota: latência é capture->entrega no DataChannel LOCAL (mesmo processo/máquina,");
        Console.WriteLine("sem STUN/TURN real, sem rede). Não é uma medição de RTT de rede — ver SPIKE-017 §Pendências.");
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
