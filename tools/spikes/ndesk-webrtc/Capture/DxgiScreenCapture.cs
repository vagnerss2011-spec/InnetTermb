using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NDesk.WebRtcSpike.Capture;

internal sealed record CapturedFrame(byte[] Bgra, int Width, int Height, int Stride, DateTime CapturedAtUtc);

/// <summary>
/// Captura de 1 monitor via DXGI Desktop Duplication (IDXGIOutputDuplication), caminho
/// primário de Win10/11 recomendado em ADR-016/docs/22. Sem Windows.Graphics.Capture aqui —
/// fora de escopo deste spike, que foca na biblioteca .NET de acesso ao DXGI (Vortice vs
/// SharpDX), não na comparação entre APIs de captura do Windows (isso já é distinguido em
/// docs/22 e seria outro spike).
/// </summary>
internal sealed unsafe class DxgiScreenCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly OutputDescription _outputDescription;

    public int Width => _outputDescription.DesktopCoordinates.Right - _outputDescription.DesktopCoordinates.Left;
    public int Height => _outputDescription.DesktopCoordinates.Bottom - _outputDescription.DesktopCoordinates.Top;

    public DxgiScreenCapture(int outputIndex = 0)
    {
        var featureLevels = new[] { FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out _device,
            out _context).CheckError();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        adapter.EnumOutputs((uint)outputIndex, out var output).CheckError();
        using (output)
        {
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            _outputDescription = output.Description;
            _duplication = output1.DuplicateOutput(_device);
        }
    }

    /// <summary>
    /// Bloqueia até <paramref name="timeoutMs"/> por um novo frame. Retorna null em timeout
    /// (tela estática — DXGI só entrega frame em mudança, por design; ver docs/22 "Performance
    /// em conexão lenta" sobre desduplicação de frames).
    /// </summary>
    public CapturedFrame? TryCaptureNextFrame(int timeoutMs)
    {
        var hr = _duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var desktopResource);
        if (hr.Failure)
        {
            if (hr == Vortice.DXGI.ResultCode.WaitTimeout)
                return null;
            throw new InvalidOperationException($"AcquireNextFrame falhou: {hr}");
        }

        try
        {
            using var texture = desktopResource!.QueryInterface<ID3D11Texture2D>();
            var desc = texture.Description;

            using var staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                Format = desc.Format,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
            });

            _context.CopyResource(staging, texture);
            _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
            try
            {
                int height = (int)desc.Height;
                int rowBytes = (int)desc.Width * 4;
                var buffer = new byte[rowBytes * height];
                var src = (byte*)mapped.DataPointer;
                for (int y = 0; y < height; y++)
                    Marshal.Copy((IntPtr)(src + y * mapped.RowPitch), buffer, y * rowBytes, rowBytes);

                return new CapturedFrame(buffer, (int)desc.Width, height, rowBytes, DateTime.UtcNow);
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
        finally
        {
            _duplication.ReleaseFrame();
            desktopResource?.Dispose();
        }
    }

    public void Dispose()
    {
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
