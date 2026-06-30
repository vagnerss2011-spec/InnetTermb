# Sprint 05 — NDesk agente legado e performance

Você é o agente responsável por validar e iniciar o agente temporário NDesk com foco em Windows 10/7, NAT e conexão lenta.

## Leitura obrigatória

- `docs/09-acesso-remoto-ndesk.md`
- `docs/22-ndesk-performance-legacy-windows.md`
- `adr/ADR-007-ndesk-agente-legado-win32.md`
- `contracts/ndesk-ticket.schema.json`
- `contracts/ndesk-permission-grant.schema.json`
- `contracts/ndesk-session-telemetry.schema.json`
- `docs/05-seguranca-credenciais-threat-model.md`

## Objetivo

Criar uma PoC controlada do agente temporário nativo, com tela de consentimento, captura básica, transporte via relay local/simulado e telemetria mínima.

## Entregas

- Skeleton `RemoteOps.NDesk.Agent`.
- UI nativa de consentimento.
- Captura GDI BitBlt para Windows legado.
- Abstração `CaptureEngine` para Windows 10/11 e legado.
- Modelo de permissão básico/controle/admin.
- Telemetria de RTT, FPS e CPU.
- Documento de resultado do spike.

## Restrições

- Sem modo oculto.
- Sem persistência silenciosa.
- Sem captura de senha.
- Sem bypass de UAC.
- Sem exigir Java, WebView2 ou .NET moderno no agente.

## Critério de aceite

- Agente abre uma janela de consentimento.
- Agente inicia captura somente depois de consentimento.
- Agente roda em laboratório Windows 10 e Windows 7 SP1.
- Usuário consegue encerrar imediatamente.
- Métricas básicas são emitidas sem conteúdo de tela.
