# ADR-012 — WebView2 + xterm.js como UI do terminal SSH/Telnet

> Renumerado de ADR-011 → ADR-012 na integração (INT-1 já usa ADR-011 para Dependency Injection no Desktop).

## Status

Aceita

## Contexto

O RemoteOps Desktop precisa de uma aba de terminal funcional para SSH e Telnet.
Os requisitos mínimos são: suporte completo a VT100/ANSI (cores de 256 bits, cursor,
sequências de escape, Unicode), scrollback, resize de PTY, e desempenho aceitável
para saídas de alta velocidade (builds, logs em tempo real). A stack é WPF / .NET 10
no Windows; a decisão é restrita à camada de apresentação — os provedores de sessão
(SSH.NET, Telnet custom) já foram decididos no ADR-009.

O projeto também exige que nenhuma requisição de CDN ocorra em runtime: todos os
assets devem ser locais.

## Decisão

Usar **`Microsoft.Web.WebView2`** (controle WPF que hospeda Chromium via Edge WebView2
Runtime) para renderizar **xterm.js 5.x** + **xterm-addon-fit 0.8.x**, servidos de
uma pasta local via Virtual Host Name (`SetVirtualHostNameToFolderMapping`).

A comunicação entre o processo .NET e o JavaScript usa exclusivamente
`PostWebMessageAsString` / `WebMessageReceived` (bridge nativa do WebView2):
- C# → JS: chunks de saída codificados em Base64 (`{"type":"output","data":"..."}`)
- JS → C#: input do usuário e eventos de resize em Base64 / int

A pasta `Terminal/wwwroot/` contém `index.html` + `js/terminal.bundle.js` (gerado
por `esbuild` a partir dos pacotes npm) + `css/xterm.css`. Os arquivos compilados
são versionados no repositório; a pipeline de build npm é documentada em `wwwroot/`
(executada uma vez por desenvolvedor ou no CI com `npm ci && npm run build`).

## Consequências positivas

- Suporte completo a VT100/ANSI/xterm-256color, incluindo mouse, bracketed paste e OSC.
- Renderer WebGL do xterm.js: alta performance mesmo em saída de alta velocidade.
- Mesma UI para SSH e Telnet (protocolo é transparente para xterm.js).
- xterm.js é o padrão de mercado (VS Code, GitHub, JupyterLab, Cloudflare) — ecossistema maduro.
- Runtime WebView2 evergreen no Windows 11 (pré-instalado via Edge); sem dependência extra na maioria dos alvos.
- Limite de segurança forte: Virtual Host local + CSP `default-src 'none'` + PostWebMessage tipado.

## Consequências negativas

- Dependência do WebView2 Runtime (~130 MB); pode exigir instalação em Windows 10 antigos.
- Cada `WebView2` controle inicia processos filhos de browser (1 renderer por aba). Em 10 abas simultâneas: ~10 processos extras (~20-40 MB RAM cada).
- `EnsureCoreWebView2Async` tem latência de ~200 ms na primeira chamada (frio).
- O `TabControl` padrão do WPF virtualiza conteúdo: o WebView2 é recriado ao retornar a uma aba. A sessão sobrevive no `TerminalTabViewModel` (pump independente), mas o terminal visual recomeça vazio. Mitigação futura: non-virtualizing TabControl (não incluso no escopo do INT-2).
- Pipeline npm adicional: `npm ci && npm run build` obrigatório antes de alterar os assets frontend.

## Alternativas consideradas

| Alternativa | Motivo de descarte |
|-------------|-------------------|
| `RichTextBox` WPF customizado | Sem suporte a VT100/ANSI; implementação de parser de escape codes seria O(anos). |
| `Windows Terminal` SDK | Não é uma biblioteca embeddável; é uma aplicação standalone (Win32 + WinUI). |
| FluentTerminal (UWP) | Só funciona em UWP; não embeddável em WPF/.NET 10. |
| Componente VTE/libvte via P/Invoke | Primeiro suporte Linux; port Windows instável; sem manutenção ativa. |
| Electron como processo separado | Aumenta footprint, cria canal de IPC adicional, complica o modelo de segurança. |
| ActiveX WebBrowser (MSHTML) | Engine obsoleta (IE), sem suporte a ES2020+; xterm.js requer recursos modernos. |

## Detalhes de segurança

- **DevTools**: desabilitados em Release (`#if !DEBUG`).
- **Context menu**: desabilitado.
- **Host objects**: `AreHostObjectsAllowed = false` (bridge apenas via PostWebMessage).
- **CSP**: `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'` — sem inline JS, sem rede externa.
- **Virtual Host**: `https://terminal.local/` mapeado para `Terminal/wwwroot/` no output dir. Nenhuma requisição de rede real ocorre.
- **Dados externos**: o terminal usa `term.write(Uint8Array)` — não `innerHTML`. Dados do servidor remoto **nunca** injetados como HTML.
- **Segredos**: nenhum segredo trafega pelo bridge; o xterm.js recebe apenas bytes brutos de output PTY (codificados em Base64).

## Critérios de revisão

- Revisar se Windows Terminal SDK tornar-se embeddável em WPF (rastrear releases WinUI).
- Revisar se a latência de cold start do WebView2 ultrapassar 500 ms em máquinas-alvo (monitorar via telemetria).
- Revisar o não-virtualizing TabControl quando o backlog de UX incluir "preservar estado de scroll em tab switch".
