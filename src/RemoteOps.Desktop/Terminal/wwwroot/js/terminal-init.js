'use strict';

var term = new window.Terminal({
    cursorBlink: true,
    scrollback: 5000,
    theme: {
        background: '#1e1e1e',
        foreground: '#d4d4d4',
        cursor:     '#d4d4d4',
        selectionBackground: '#264f78',
    },
    fontFamily: "'Cascadia Code', 'Consolas', 'Courier New', monospace",
    fontSize: 14,
});

var fitAddon = new window.FitAddon();
term.loadAddon(fitAddon);
term.open(document.getElementById('terminal-container'));
fitAddon.fit();

// ── Input: xterm → C# ────────────────────────────────────────────────
term.onData(function (data) {
    var bytes = new TextEncoder().encode(data);
    // Use a loop instead of apply(null, bytes) — apply blows the call stack
    // when bytes.length exceeds ~65 536 (large pastes, router config blocks).
    var binary = '';
    for (var i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
    window.chrome.webview.postMessage(JSON.stringify({ type: 'input', data: btoa(binary) }));
});

// ── Resize: xterm/FitAddon → C# ──────────────────────────────────────
term.onResize(function (size) {
    window.chrome.webview.postMessage(
        JSON.stringify({ type: 'resize', cols: size.cols, rows: size.rows }));
});

// ── Output: C# → xterm ───────────────────────────────────────────────
window.chrome.webview.addEventListener('message', function (e) {
    try {
        var msg = JSON.parse(e.data);
        if (msg.type === 'output') {
            // Decode base64 → Uint8Array → xterm (handles binary/UTF-8)
            var raw = atob(msg.data);
            var bytes = new Uint8Array(raw.length);
            for (var i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
            term.write(bytes);
        }
    } catch (_) { /* ignore malformed messages */ }
});

// ── Window resize → FitAddon → onResize → C# ─────────────────────────
var resizeObserver = new ResizeObserver(function () { fitAddon.fit(); });
resizeObserver.observe(document.getElementById('terminal-container'));
