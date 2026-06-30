'use strict';

(function () {
  // ── Terminal setup ──────────────────────────────────────────────────────────
  const term = new Terminal({
    cursorBlink: true,
    fontSize: 14,
    fontFamily: 'Cascadia Code, Consolas, monospace',
    theme: {
      background: '#1e1e1e',
      foreground: '#d4d4d4',
      cursor:     '#aeafad',
    },
    devicePixelRatio: window.devicePixelRatio || 1,
    allowProposedApi: true,
  });

  const fitAddon = new FitAddon.FitAddon();
  term.loadAddon(fitAddon);
  term.open(document.getElementById('terminal-container'));
  fitAddon.fit();

  // ── Resize observer ─────────────────────────────────────────────────────────
  const ro = new ResizeObserver(() => {
    fitAddon.fit();
    postToHost({ type: 'resize', cols: term.cols, rows: term.rows });
  });
  ro.observe(document.getElementById('terminal-container'));

  // ── Input from xterm → C# ──────────────────────────────────────────────────
  term.onData(data => {
    const bytes = new TextEncoder().encode(data);
    postToHost({ type: 'input', payload: btoa(String.fromCharCode(...bytes)) });
  });

  // ── Copy/paste ──────────────────────────────────────────────────────────────
  // xterm.js handles copy via selection; we wire Ctrl+Shift+C and Ctrl+Shift+V.
  term.attachCustomKeyEventHandler(e => {
    if (e.type !== 'keydown') return true;

    if (e.ctrlKey && e.shiftKey && e.key === 'C') {
      const sel = term.getSelection();
      if (sel) navigator.clipboard.writeText(sel).catch(() => {});
      return false;
    }

    if (e.ctrlKey && e.shiftKey && e.key === 'V') {
      navigator.clipboard.readText().then(text => {
        if (!text) return;
        const bytes = new TextEncoder().encode(text);
        postToHost({ type: 'input', payload: btoa(String.fromCharCode(...bytes)) });
      }).catch(() => {});
      return false;
    }

    return true;
  });

  // ── Messages from C# → xterm ────────────────────────────────────────────────
  window.chrome.webview.addEventListener('message', e => {
    const msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;

    switch (msg.type) {
      case 'data':
        term.write(Uint8Array.from(atob(msg.payload), c => c.charCodeAt(0)));
        break;

      case 'connected':
        term.writeln('\r\x1b[32m● Conectado\x1b[0m');
        break;

      case 'disconnected':
        term.writeln(`\r\n\x1b[33m⊘ ${msg.reason}\x1b[0m`);
        break;

      case 'error':
        term.writeln(`\r\n\x1b[31m✗ Erro: ${msg.message}\x1b[0m`);
        break;

      case 'hostkey-prompt':
        showHostKeyDialog(msg);
        break;

      case 'telnet-warning':
        showTelnetWarning(msg.message);
        break;
    }
  });

  // ── Host-key dialog ─────────────────────────────────────────────────────────
  function showHostKeyDialog(msg) {
    const dialog  = document.getElementById('hostkey-dialog');
    const title   = document.getElementById('hostkey-title');
    const hostEl  = document.getElementById('hostkey-host');
    const fpEl    = document.getElementById('hostkey-fp');
    const typeEl  = document.getElementById('hostkey-type');
    const statEl  = document.getElementById('hostkey-status');

    title.textContent  = msg.hasChanged ? '⚠ Host Key ALTERADA — Possível MITM!' : 'Nova Host Key SSH';
    hostEl.textContent = `Host: ${msg.host}`;
    fpEl.textContent   = `SHA-256: ${msg.fingerprint}`;
    typeEl.textContent = `Tipo: ${msg.keyType}`;

    if (msg.hasChanged) {
      const span = document.createElement('span');
      span.className = 'danger';
      span.textContent = 'A chave deste host MUDOU. Verifique se é esperado antes de aceitar.';
      statEl.textContent = '';
      statEl.appendChild(span);
    } else if (msg.isKnown) {
      statEl.textContent = 'Chave conhecida e válida.';
    } else {
      statEl.textContent = 'Host desconhecido — primeira conexão.';
    }

    dialog.classList.add('visible');

    document.getElementById('btn-accept').onclick = () => {
      dialog.classList.remove('visible');
      postToHost({ type: 'hostkey-accept' });
    };
    document.getElementById('btn-reject').onclick = () => {
      dialog.classList.remove('visible');
      postToHost({ type: 'hostkey-reject' });
    };
  }

  // ── Telnet warning banner ───────────────────────────────────────────────────
  function showTelnetWarning(message) {
    const banner = document.getElementById('telnet-warning');
    document.getElementById('telnet-warning-text').textContent = message;
    banner.classList.add('visible');
    document.getElementById('telnet-warning-ok').onclick = () => {
      banner.classList.remove('visible');
    };
  }

  // ── Helpers ─────────────────────────────────────────────────────────────────
  function postToHost(obj) {
    window.chrome.webview.postMessage(JSON.stringify(obj));
  }
})();
