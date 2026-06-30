namespace RemoteOps.MikroTik;

public sealed class WinBoxValidationException : Exception
{
    public WinBoxValidationException(string message) : base(message) { }
    public WinBoxValidationException(string message, Exception inner) : base(message, inner) { }
}
