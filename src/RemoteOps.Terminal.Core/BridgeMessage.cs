using System.Text.Json.Serialization;

namespace RemoteOps.Terminal.Core;

/// <summary>
/// JSON message schema for the WebView2 ↔ C# bridge.
/// JS sends to C# via window.chrome.webview.postMessage(JSON.stringify(msg)).
/// C# sends to JS via CoreWebView2.PostWebMessageAsJson(json).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(InputMessage),       "input")]
[JsonDerivedType(typeof(ResizeMessage),      "resize")]
[JsonDerivedType(typeof(HostKeyAcceptMsg),   "hostkey-accept")]
[JsonDerivedType(typeof(HostKeyRejectMsg),   "hostkey-reject")]
[JsonDerivedType(typeof(DataMessage),        "data")]
[JsonDerivedType(typeof(ConnectedMessage),   "connected")]
[JsonDerivedType(typeof(DisconnectedMessage),"disconnected")]
[JsonDerivedType(typeof(HostKeyPromptMsg),   "hostkey-prompt")]
[JsonDerivedType(typeof(TelnetWarningMsg),   "telnet-warning")]
[JsonDerivedType(typeof(ErrorMessage),       "error")]
public abstract record BridgeMessage;

// ── JS → C# ──────────────────────────────────────────────────────────────────

/// <summary>Keyboard/paste input from xterm.js (base64-encoded bytes).</summary>
public sealed record InputMessage([property: JsonPropertyName("payload")] string Payload) : BridgeMessage;

/// <summary>xterm.js FitAddon detected a resize.</summary>
public sealed record ResizeMessage(
    [property: JsonPropertyName("cols")] int Cols,
    [property: JsonPropertyName("rows")] int Rows) : BridgeMessage;

public sealed record HostKeyAcceptMsg : BridgeMessage;
public sealed record HostKeyRejectMsg : BridgeMessage;

// ── C# → JS ──────────────────────────────────────────────────────────────────

/// <summary>Data bytes from the remote (base64-encoded).</summary>
public sealed record DataMessage([property: JsonPropertyName("payload")] string Payload) : BridgeMessage;

public sealed record ConnectedMessage : BridgeMessage;

public sealed record DisconnectedMessage(
    [property: JsonPropertyName("reason")] string Reason) : BridgeMessage;

public sealed record HostKeyPromptMsg(
    [property: JsonPropertyName("host")]        string Host,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("keyType")]     string KeyType,
    [property: JsonPropertyName("isKnown")]     bool IsKnown,
    [property: JsonPropertyName("hasChanged")]  bool HasChanged) : BridgeMessage;

public sealed record TelnetWarningMsg(
    [property: JsonPropertyName("message")] string Message) : BridgeMessage;

public sealed record ErrorMessage(
    [property: JsonPropertyName("message")] string Message) : BridgeMessage;
