using System.Runtime.InteropServices;

namespace NDesk.WebRtcSpike.Transport;

/// <summary>
/// Bindings P/Invoke mínimos contra a API C pública do libdatachannel (rtc.h, MPL-2.0).
/// Escritos à mão contra https://github.com/paullouisageneau/libdatachannel — não usamos
/// nenhum wrapper gerenciado de terceiro; só o binário nativo datachannel.dll é de terceiro.
/// Cobre apenas o subconjunto necessário para 2 PeerConnections locais com 1 DataChannel.
/// </summary>
internal static class LibDataChannelNative
{
    private const string Lib = "datachannel";

    [StructLayout(LayoutKind.Sequential)]
    public struct RtcConfiguration
    {
        public IntPtr iceServers;
        public int iceServersCount;
        public IntPtr proxyServer;
        public IntPtr bindAddress;
        public int certificateType;
        public IntPtr certificatePemFile;
        public IntPtr keyPemFile;
        public IntPtr keyPemPass;
        public int iceTransportPolicy;
        [MarshalAs(UnmanagedType.I1)] public bool enableIceTcp;
        [MarshalAs(UnmanagedType.I1)] public bool enableIceUdpMux;
        [MarshalAs(UnmanagedType.I1)] public bool disableAutoNegotiation;
        [MarshalAs(UnmanagedType.I1)] public bool forceMediaTransport;
        public ushort portRangeBegin;
        public ushort portRangeEnd;
        public int mtu;
        public int maxMessageSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DescriptionCallback(int pc, string sdp, string type, IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CandidateCallback(int pc, string cand, string mid, IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StateChangeCallback(int pc, int state, IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DataChannelCallback(int pc, int dc, IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OpenCallback(int id, IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ClosedCallback(int id, IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MessageCallback(int id, IntPtr message, int size, IntPtr ptr);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcCreatePeerConnection(ref RtcConfiguration config);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcClosePeerConnection(int pc);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcDeletePeerConnection(int pc);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcSetLocalDescriptionCallback(int pc, DescriptionCallback cb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcSetLocalCandidateCallback(int pc, CandidateCallback cb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcSetStateChangeCallback(int pc, StateChangeCallback cb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcSetDataChannelCallback(int pc, DataChannelCallback cb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int rtcCreateDataChannel(int pc, string label);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcSetOpenCallback(int id, OpenCallback cb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcSetClosedCallback(int id, ClosedCallback cb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcSetMessageCallback(int id, MessageCallback cb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcSendMessage(int id, byte[] data, int size);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int rtcSetRemoteDescription(int pc, string sdp, string? type);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int rtcAddRemoteCandidate(int pc, string cand, string? mid);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcDeleteDataChannel(int dc);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcSetUserPointer(int id, IntPtr ptr);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcMaxMessageSize(int id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtcGetBufferedAmount(int id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool rtcIsOpen(int id);
}
