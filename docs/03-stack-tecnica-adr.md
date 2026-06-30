# 03 — Stack técnica e decisões iniciais

## Resumo da decisão

A recomendação inicial é usar **C#/.NET 10 + WPF** como base do **desktop principal Windows 10/11** e **ASP.NET Core** como backend. O módulo NDesk deve ser separado em duas partes: viewer integrado ao desktop e agente temporário nativo Win32/C++ para máquinas atendidas, inclusive Windows 7 legado, sem depender de Java, WebView2 ou .NET moderno.

## Separação por tipo de componente

| Componente | Stack recomendada | Motivo |
|---|---|---|
| RemoteOps Desktop | C#/.NET 10 + WPF + MVVM | UI Windows rica, produtividade, interop com ActiveX/RDP |
| Terminal embutido | WebView2 + xterm.js | Terminal moderno em abas para SSH/Telnet |
| Backend cloud/sync | ASP.NET Core + PostgreSQL + SignalR | Sincronização multiusuário e realtime |
| NDesk Viewer | Integrado ao Desktop WPF | Operador usa a mesma aplicação interna |
| NDesk Temporary Agent | C++/Win32 nativo, runtime estático quando possível | Rodar em Windows 7/10 sem Java/WebView2/.NET moderno |
| NDesk Relay | Go, Rust ou C# worker, a decidir em spike | Serviço de rede de alta disponibilidade |
| Worker de mídia opcional | C++ ou Rust, se compatível com alvo | Captura/codec/input com performance |

## Por que .NET/WPF no desktop principal

- O produto principal é Windows-only.
- WPF é maduro para aplicações desktop Windows complexas.
- Integra bem com Windows Forms/ActiveX, necessário para hospedar o controle RDP da Microsoft.
- WebView2 em WPF permite usar xterm.js para terminal moderno.
- C# facilita dividir trabalho entre agentes e manter produtividade alta.
- O mesmo ecossistema serve para desktop, backend, SignalR, testes e CI.

## Por que o agente NDesk não deve depender de .NET moderno

O agente temporário é baixado por usuários atendidos em máquinas diversas. Ele precisa abrir rápido e evitar pré-requisitos. Por isso:

- não exigir instalação de Java;
- não exigir instalação de .NET moderno;
- não exigir WebView2;
- evitar instalador pesado;
- assinar binário;
- empacotar dependências necessárias;
- ter modo single-file quando possível.

Para Windows 7, o uso de .NET moderno e WebView2 atual é problemático. Se algum fallback em .NET Framework 4.8 for considerado, isso deve virar ADR separado, pois pode exigir runtime instalado na máquina. A prioridade do agente temporário é Win32 nativo.

## Por que não Go como stack principal do desktop

Go é excelente para serviços, CLIs e backends leves, mas para UI Windows rica com RDP embutido, WebView2, MVVM e ActiveX a produtividade tende a cair. Pode ser considerado para relay, broker auxiliar ou ferramentas de linha de comando.

## Por que não C++ como stack principal inteira

C++ dá controle máximo, mas aumenta custo de memória, segurança, build, UI, testes e manutenção. Deve ser usado onde o ganho compensa: agente temporário NDesk, captura, codec, input, componentes de baixa dependência e performance.

## Por que não Rust como stack principal inteira

Rust é forte para segurança de memória, rede e performance. Porém, para UI Windows empresarial, RDP ActiveX e integração com ecossistema Microsoft, o custo de entrega é maior. Além disso, suporte oficial moderno para Windows 7 é um ponto de risco. Rust pode ser usado em serviços/relay ou worker nativo se o spike validar compatibilidade e build.

## Por que WPF em vez de WinUI 3 no MVP

WinUI 3 é moderno, mas o MVP precisa hospedar RDP ActiveX com menor risco. WPF possui caminhos conhecidos de interop com Windows Forms/ActiveX. O design visual pode ser modernizado com bibliotecas de estilo sem sacrificar o acesso a componentes legados.

## Stack recomendada por camada

| Camada | Escolha | Motivo |
|---|---|---|
| Desktop shell | C#/.NET 10 + WPF + MVVM | Windows-only, produtividade, interop |
| Terminal UI | WebView2 + xterm.js | Terminal robusto, estilo VS Code |
| SSH | SSH.NET inicialmente | Biblioteca .NET madura para cliente SSH |
| Telnet | Adapter TCP/Telnet próprio mínimo ou lib validada | Telnet é legado; manter isolado |
| RDP | MSTSCAX ActiveX | Reuso do stack Microsoft |
| MikroTik MVP | WinBox oficial externo via runner | Reuso do cliente oficial e redução de escopo |
| MikroTik futuro | RouterOS API-SSL/REST + SSH | UI própria e automações estruturadas |
| Local DB | SQLite + SQLCipher | Offline-first e criptografia local |
| Sync backend | ASP.NET Core + PostgreSQL | Multiusuário, transações, .NET end-to-end |
| Realtime | SignalR/WebSocket | Reativo para sync entre clientes |
| Cache broker | Redis opcional | Sessões NDesk, rate limit, pub/sub |
| NDesk transporte | WebRTC + TURN/relay; fallback TCP/TLS legado | NAT traversal e operação em rede difícil |
| NDesk capture Win10+ | Windows.Graphics.Capture/DXGI | Melhor performance em Windows moderno |
| NDesk capture Win7 | GDI BitBlt/DXGI validado em spike | Compatibilidade legada sem instalação pesada |
| CI | GitHub Actions Windows runner | Build/test Windows automatizado |

## Bibliotecas e tecnologias candidatas

### Terminal

- xterm.js no WebView2.
- Bridge C# ↔ JavaScript por WebView2 messaging.
- Multiplexação por `TerminalSessionId`.

### SSH

- SSH.NET para MVP.
- Avaliar suporte a algoritmos exigidos por equipamentos legados.
- Para equipamentos antigos, criar matriz de compatibilidade por fornecedor.

### RDP

- MSTSCAX/ActiveX no MVP.
- FreeRDP como alternativa em spike se ActiveX limitar UX, eventos ou distribuição.

### MikroTik

- WinBox oficial externo no MVP, chamado por `WinBoxRunner`.
- RouterOS API-SSL na porta 8729 quando habilitada.
- REST API para RouterOS v7 quando disponível.
- SSH como fallback universal.

### NDesk

- Broker próprio para convite/signaling.
- Relay próprio para NAT/CGNAT/firewall.
- WebRTC nativo onde viável.
- Fallback TCP/TLS 443 para agente legado.
- C++/Win32 para agente temporário de baixa dependência.

### Segurança

- DPAPI no Windows para chaves locais.
- Envelope encryption para credenciais sincronizadas.
- SQLCipher para DB local.
- OpenTelemetry/logs estruturados sem segredo.
- Assinatura de binário para Desktop, NDesk Agent e pacote de ferramentas.

## ADRs relacionados

- `adr/ADR-001-stack-principal.md`
- `adr/ADR-002-sync-offline-first.md`
- `adr/ADR-003-credenciais-e2ee.md`
- `adr/ADR-004-rdp-activex-vs-freerdp.md`
- `adr/ADR-005-acesso-remoto-webrtc.md`
- `adr/ADR-006-mikrotik-winbox-externo.md`
- `adr/ADR-007-ndesk-agente-legado-win32.md`
