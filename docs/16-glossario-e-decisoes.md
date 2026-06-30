# 16 — Glossário e decisões rápidas

## Glossário

- **Asset:** equipamento ou servidor gerenciado.
- **Endpoint:** forma de acesso a um asset, como SSH, RDP ou API.
- **CredentialRef:** referência a uma credencial, sem conter o segredo em si.
- **SecretEnvelope:** segredo criptografado.
- **Workspace:** escopo de trabalho/empresa/time.
- **Tenant:** organização maior que contém workspaces.
- **Outbox:** fila local de mudanças a enviar.
- **Changelog:** trilha de mudanças no servidor.
- **NDesk:** módulo de assistência remota consentida.
- **Broker:** serviço que pareia operador e agente NDesk.
- **Relay/TURN:** serviço que transporta mídia quando conexão direta não funciona.

## Decisões rápidas

| Tema | Decisão inicial |
|---|---|
| Desktop | WPF + .NET 10 |
| Terminal | WebView2 + xterm.js |
| SSH | SSH.NET no MVP |
| RDP | MSTSCAX/ActiveX no MVP |
| MikroTik | API-SSL/REST + SSH; não clonar WinBox |
| Sync | Offline-first com changelog |
| Backend | ASP.NET Core + PostgreSQL |
| Segredos | Envelope encryption + DPAPI local |
| NDesk | WebRTC/captura Windows com consentimento |
| CI | GitHub Actions Windows |

## Dúvidas a validar com a empresa

- Qual IdP será usado para login/MFA?
- O gerente aprova toda mudança ou apenas credenciais/permissões?
- Pode existir modo offline com credencial em cache? Por quanto tempo?
- Telnet será permitido para todos ou apenas grupos específicos?
- Exportação de inventário será permitida?
- O módulo NDesk precisa gravar sessões?
- Há exigência de LGPD/compliance interna para logs?
- Quais fabricantes exatos devem ser testados no lab?
