# 12 — Roadmap por fases

## Fase 0 — Fundação do repositório

Objetivo: criar base para todos os agentes trabalharem sem conflito.

Entregas:

- Repositório privado.
- Estrutura de pastas.
- CI inicial.
- `CLAUDE.md`, `AGENTS.md`, subagentes.
- Contratos iniciais.
- ADRs 001–007.
- Skeleton .NET solution.
- Política de versionamento e changelog.

Critério de aceite:

- CI passa.
- Build vazio/skeleton passa em Windows.
- PR template e CODEOWNERS configurados.
- `CHANGELOG.md` e `VERSIONING.md` existem.

## Fase 1 — Desktop shell + storage local

Entregas:

- WPF shell.
- Login local/mock.
- Árvore de grupos.
- CRUD local de host/grupo/endpoint/credential metadata.
- SQLite criptografado ou interface pronta para SQLCipher.
- Vault local com DPAPI.

Critério de aceite:

- Criar grupo/host localmente.
- Reiniciar app e dados persistem criptografados.
- Nenhum segredo aparece em log.

## Fase 2 — SSH/Telnet MultiTerm

Entregas:

- WebView2 + xterm.js.
- Bridge terminal.
- SSH adapter.
- Telnet adapter com política.
- Host key validation.
- Abas múltiplas.

Critério de aceite:

- Abrir 10 sessões SSH simultâneas.
- Resize funciona.
- Credencial de grupo funciona.

## Fase 3 — MikroTik WinBox Runner

Entregas:

- Perfil de host MikroTik.
- Pasta/manifesto de ferramentas externas.
- Validação de `winbox.exe` por hash.
- Montagem de argumentos IPv4/IPv6/porta/usuário/workspace.
- Política para senha via argumento.
- Auditoria de abertura WinBox.

Critério de aceite:

- Abrir WinBox oficial externo para host MikroTik.
- Abrir IPv6 com colchetes e porta customizada.
- Bloquear senha automática quando política não permite.
- Não logar senha.
- Registrar auditoria.

## Fase 4 — Cloud sync + RBAC + auditoria

Entregas:

- Backend ASP.NET Core.
- PostgreSQL.
- Auth inicial.
- Sync push/pull.
- SignalR change hints.
- Audit log.
- Multiusuário.
- Aprovação gerencial para alterações sensíveis.

Critério de aceite:

- Usuário A cria host; usuário B recebe.
- Offline outbox sincroniza ao reconectar.
- Auditoria registra mudança.
- Permissões bloqueiam ação não autorizada.

## Fase 5 — RDP

Entregas:

- MSTSCAX hospedado em WPF.
- Credential mapping.
- Políticas de redirecionamento.
- Eventos de conexão/desconexão.

Critério de aceite:

- Abrir RDP em aba contra Windows Server.
- Porta customizada funciona.
- Clipboard/drive obedecem política.

## Fase 6 — MikroTik estruturado e fornecedores

Entregas:

- RouterOS API-SSL.
- RouterOS REST quando disponível.
- Telas MikroTik iniciais.
- Perfis Cisco/Huawei/Juniper/ZTE.

Critério de aceite:

- Consultar interfaces e identidade MikroTik.
- Abrir SSH MikroTik.
- Aplicar perfil de paging opcional.

## Fase 7 — NDesk MVP

Entregas:

- Broker de convite.
- Agente temporário assinado Win32.
- Consentimento.
- Screen share.
- Controle opcional.
- Relay próprio.
- Auditoria.

Critério de aceite:

- Link expira.
- Usuário autoriza visualização.
- Usuário autoriza controle separadamente.
- Usuário encerra sessão.
- Relay funciona atrás de NAT/CGNAT.
- Agente roda em Windows 10 e Windows 7 SP1 de laboratório sem Java/WebView2/.NET moderno.

## Fase 8 — NDesk robusto para conexão lenta e admin consentido

Entregas:

- Perfis de qualidade.
- Adaptação de bitrate/FPS/resolução.
- Telemetria de sessão.
- Fallback TCP/TLS 443.
- Modo administrador consentido com helper temporário.
- Testes com rede degradada.

Critério de aceite:

- Sessão utilizável em 2 Mbps upload e 80 ms RTT.
- Controle priorizado sobre vídeo.
- Modo administrador exige consentimento e respeita UAC.
- Métricas mostram rota, RTT, perda, FPS e bitrate.

## Fase 9 — Hardening e operação

Entregas:

- Instalador assinado.
- Auto-update controlado.
- Monitoramento.
- Backup/restore.
- Testes de carga e segurança.
- Documentação operacional.
- Release notes internas.

Critério de aceite:

- Instalação limpa em máquinas piloto.
- Atualização funciona.
- Restore testado.
- Checklist de segurança aprovado.
- Changelog da versão aprovado.

## Prioridade recomendada

Começar por fundação, storage, contratos, CI, changelog, shell e SSH. Em paralelo, fazer spike do WinBox Runner e spike do agente NDesk legado, porque ambos reduzem risco cedo. Não começar NDesk completo antes de estabilizar credenciais, RBAC, auditoria e sync.
