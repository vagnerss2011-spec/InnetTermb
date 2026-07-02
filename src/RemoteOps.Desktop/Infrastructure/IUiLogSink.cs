namespace RemoteOps.Desktop.Infrastructure;

public interface IUiLogSink
{
    void Emit(string line);
}
