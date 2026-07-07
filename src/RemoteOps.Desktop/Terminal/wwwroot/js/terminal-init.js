'use strict';

// Todo o init roda dentro de um IIFE com guarda: se o bundle do xterm não carregar
// (CSP, cópia truncada no publish, erro de script), o terminal NÃO fica em branco em
// silêncio — mostra o erro no container e avisa o C# (type:init_error). Antes, um
// window.Terminal undefined lançava e a aba ficava preta sem nenhuma pista.
(function () {
    var container = document.getElementById('terminal-container');

    function showError(text) {
        if (container) {
            container.textContent = text;
            container.style.color = '#f48771';
            container.style.font = "14px 'Consolas', 'Courier New', monospace";
            container.style.padding = '12px';
            container.style.whiteSpace = 'pre-wrap';
        }
        try {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'init_error', message: text }));
        } catch (_) { /* bridge indisponível */ }
    }

    if (typeof window.Terminal === 'undefined' || typeof window.FitAddon === 'undefined') {
        showError('Falha ao carregar o terminal: biblioteca xterm não encontrada. Reinstale o RemoteOps pelo instalador (Setup.exe).');
        return;
    }

    var term, fitAddon;
    try {
        term = new window.Terminal({
            cursorBlink: true,
            scrollback: 5000,
            theme: {
                background: '#1e1e1e',
                foreground: '#d4d4d4',
                cursor: '#d4d4d4',
                selectionBackground: '#264f78',
            },
            fontFamily: "'Cascadia Code', 'Consolas', 'Courier New', monospace",
            fontSize: 14,
        });
        fitAddon = new window.FitAddon();
        term.loadAddon(fitAddon);
        term.open(container);
    } catch (err) {
        showError('Erro ao iniciar o terminal: ' + (err && err.message ? err.message : err));
        return;
    }

    // fit() lança se o container ainda não tem tamanho (aba sendo montada) — engolir e
    // deixar o ResizeObserver/activate reajustar depois evita o terminal "em branco".
    function fitSafe() {
        try { fitAddon.fit(); } catch (_) { /* container sem tamanho ainda */ }
    }

    // Reajusta o tamanho E devolve o foco do teclado ao terminal. Chamado no init (após o
    // layout), quando a janela recebe foco, e pelo C# (window.__roActivate) toda vez que a
    // aba do terminal fica ativa — antes o operador tinha que clicar dentro pra digitar e o
    // conteúdo às vezes só aparecia depois de uma interação (fit prematuro).
    function activate() {
        fitSafe();
        try { term.focus(); } catch (_) { /* terminal fechado */ }
    }

    // Ajusta/foca DEPOIS do layout (dois rAF garantem que o container já tem tamanho real).
    requestAnimationFrame(function () { requestAnimationFrame(activate); });

    // C# chama isto quando a aba do terminal fica ativa/visível (troca de aba).
    window.__roActivate = activate;

    // Voltar o foco para a janela deve reajustar + focar o terminal.
    window.addEventListener('focus', activate);

    // Clique dentro do terminal foca o teclado (comportamento esperado do xterm).
    if (container) {
        container.addEventListener('mousedown', function () {
            try { term.focus(); } catch (_) { /* terminal fechado */ }
        });
    }

    // ── Input: xterm → C# ────────────────────────────────────────────────
    term.onData(function (data) {
        var bytes = new TextEncoder().encode(data);
        // Loop em vez de apply(null, bytes): apply estoura a pilha acima de ~65 536
        // bytes (colar blocos grandes de config de router).
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
                var raw = atob(msg.data);
                var bytes = new Uint8Array(raw.length);
                for (var i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
                term.write(bytes);
            }
        } catch (_) { /* mensagem malformada */ }
    });

    // ── Window resize → FitAddon → onResize → C# ─────────────────────────
    var resizeObserver = new ResizeObserver(function () { fitSafe(); });
    resizeObserver.observe(container);

    // Sinaliza ao C# que o terminal está pronto (xterm criado com sucesso).
    try {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'ready' }));
    } catch (_) { /* bridge indisponível */ }
})();
