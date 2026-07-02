# Instruções para Claude Code — RemoteOps Suite

> **Retomando o projeto?** Leia `CONTINUAR-LOCAL.md` — estado atual, como abrir uma sessão do
> Claude local que edita esta pasta, como está organizado (worktrees/CI) e como rodar/validar.

Você está trabalhando em um produto interno Windows para gestão de acessos remotos de infraestrutura. O projeto é sensível porque armazena credenciais, abre sessões administrativas e poderá executar assistência remota consentida.

## Princípios obrigatórios

1. Nunca grave senha, chave privada, token ou segredo em texto puro.
2. Nunca implemente acesso remoto oculto, persistência silenciosa, bypass de consentimento, evasão de antivírus ou coleta de credenciais.
3. Todo acesso remoto estilo NDesk deve exigir consentimento visível, revogação imediata e auditoria.
4. Toda mudança de contrato, schema, API ou criptografia exige ADR em `adr/`.
5. Toda feature deve incluir testes, logs sem segredos e critérios de aceite.
6. Não misture módulos em uma mesma PR quando puder separar.
7. Atualize a documentação afetada no mesmo PR da mudança.

## Arquitetura resumida

- Desktop: C#/.NET 10 + WPF + MVVM.
- Terminal: WebView2 + xterm.js + backend de stream por protocolo.
- RDP: MSTSCAX/ActiveX hospedado via WPF/WinForms interop; FreeRDP em spike.
- Sync: ASP.NET Core + PostgreSQL + SignalR/WebSocket.
- Local DB: SQLite criptografado/SQLCipher com outbox de mudanças.
- Segurança: envelope encryption, DPAPI no Windows, RBAC, auditoria.
- NDesk: módulo separado com signaling, relay/WebRTC, consentimento e worker nativo opcional.

## Fluxo de trabalho

Antes de codar:

1. Leia o documento do módulo em `docs/`.
2. Leia os ADRs existentes.
3. Verifique contratos em `contracts/`.
4. Crie plano curto no PR.

Durante a implementação:

1. Trabalhe em uma branch `feature/<modulo>-<descricao>`.
2. Faça commits pequenos.
3. Rode testes locais.
4. Não modifique pastas de outro módulo sem mencionar o agente responsável.

Antes de finalizar:

1. Rode `dotnet format` quando houver projeto .NET.
2. Rode `dotnet test` quando houver testes .NET.
3. Rode testes de frontend quando houver pacote JS.
4. Atualize `docs/` e `adr/` quando necessário.
5. Não deixe TODOs críticos sem issue associada.

## Subagentes sugeridos

Use explicitamente os subagentes em `.claude/agents/`:

- `remoteops-architect`
- `desktop-shell-agent`
- `ssh-telnet-agent`
- `mikrotik-agent`
- `rdp-agent`
- `cloud-sync-agent`
- `security-agent`
- `ndesk-agent`
- `qa-agent`
- `devops-agent`
- `research-agent`
- `docs-agent`

## Definition of Done geral

- Build passando no Windows.
- Testes automatizados novos ou atualizados.
- Sem segredos em log, commit, fixture ou screenshot.
- Auditoria de eventos sensíveis implementada ou issue criada.
- Documentação atualizada.
- PR pequeno o suficiente para revisão humana.

## Atualizações de arquitetura v2

- MikroTik no MVP usa WinBox oficial externo via `WinBoxRunner`; leia `docs/21-mikrotik-winbox-runner.md` e `adr/ADR-006-mikrotik-winbox-externo.md` antes de alterar este módulo.
- NDesk terá agente temporário Win32/C++ separado do Desktop principal para Windows 10/7, sem Java, WebView2 ou .NET moderno; leia `docs/22-ndesk-performance-legacy-windows.md` e `adr/ADR-007-ndesk-agente-legado-win32.md`.
- Toda mudança visível precisa atualizar `CHANGELOG.md`.
- Mudança em contrato público exige atualização de `contracts/`, docs e ADR.
- Recursos sensíveis devem usar feature flags e revisão do `security-agent`.

