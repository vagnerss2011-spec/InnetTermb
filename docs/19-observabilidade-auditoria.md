# 19 — Observabilidade e auditoria

## Objetivo

Permitir operação confiável e investigação sem vazar segredos.

## Logs de aplicação

- Estruturados em JSON no backend.
- Arquivos locais rotacionados no desktop.
- Correlation ID por fluxo.
- Device ID em eventos autenticados.
- Nunca logar senha, chave privada, token, link NDesk completo ou payload de segredo.

## Audit log

Audit log é diferente de log de debug. Deve ser persistente, consultável e protegido contra alteração indevida.

### Eventos mínimos

- Login/logout.
- Falha de login.
- Dispositivo registrado/revogado.
- Host/grupo criado/alterado/excluído.
- Credencial criada/rotacionada/usada.
- Sessão SSH/Telnet/RDP aberta/fechada.
- Convite NDesk criado/usado/expirado.
- Permissão NDesk concedida/revogada.
- Exportação/importação.
- Alteração de política.

## Métricas backend

- Request rate.
- Error rate.
- P95 latency.
- Sync push/pull latency.
- Sync lag por cliente.
- Active WebSocket connections.
- Active NDesk tickets.
- Relay bandwidth.
- Failed auth attempts.

## Métricas desktop

- Tempo de inicialização.
- Tempo para abrir sessão.
- Erros por protocolo.
- Fila outbox pendente.
- Uso de memória por abas.

## Redaction

Implementar sanitizer central para:

- `password`
- `privateKey`
- `token`
- `authorization`
- `cookie`
- `secret`
- `linkToken`
- `ciphertext` quando não necessário

## Retenção

- Logs locais: 7–30 dias, configurável.
- Audit central: conforme política interna, recomendado mínimo 180 dias.
- NDesk recording, se existir: retenção separada e consentimento/política clara.

## Alertas iniciais

- Muitas falhas de login.
- Usuário revogado tentando acessar.
- Mudança de credencial de grupo.
- Revelação de senha.
- Exportação de inventário.
- Sessão NDesk fora de horário.
- Erro de sync elevado.

## Eventos adicionais v2

### WinBox Runner

- `winbox_tool_validated`
- `winbox_open_requested`
- `winbox_open_started`
- `winbox_open_failed`
- `winbox_password_argument_used`
- `winbox_ipv6_target_used`
- `winbox_ipv4_fallback_used`
- `winbox_romon_used`

Os eventos WinBox nunca devem conter senha ou linha de comando completa.

### NDesk performance e permissões

- `ndesk_route_selected`
- `ndesk_route_changed`
- `ndesk_low_bandwidth_mode_enabled`
- `ndesk_admin_requested`
- `ndesk_admin_granted`
- `ndesk_admin_denied`
- `ndesk_admin_revoked`
- `ndesk_agent_legacy_windows_started`
- `ndesk_agent_shutdown_by_user`

Métricas NDesk não devem conter imagem de tela, texto visível ou nomes de arquivos completos quando isso for sensível. Para transferência, registrar metadados mínimos e hash quando permitido.
