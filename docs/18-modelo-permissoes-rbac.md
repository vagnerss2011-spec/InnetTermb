# 18 — Modelo de permissões e RBAC

## Escopos

- Tenant.
- Workspace.
- Grupo de assets.
- Asset.
- Endpoint.
- Credencial.
- Ferramenta externa.
- Sessão NDesk.

## Permissões granulares

### Inventário

- `asset.read`
- `asset.create`
- `asset.update`
- `asset.delete`
- `asset.move`
- `asset.export`

### Credenciais

- `credential.readMetadata`
- `credential.create`
- `credential.updateMetadata`
- `credential.rotate`
- `credential.use`
- `credential.reveal`
- `credential.grant`
- `credential.revoke`

### Sessões

- `session.ssh.open`
- `session.telnet.open`
- `session.rdp.open`
- `session.mikrotik.api`
- `session.mikrotik.winbox.open`
- `session.mikrotik.winbox.passwordArgument`
- `session.ndesk.createTicket`
- `session.ndesk.view`
- `session.ndesk.control`
- `session.ndesk.fileTransfer`
- `session.ndesk.adminRequest`
- `session.ndesk.adminApprove`

### Administração

- `user.invite`
- `user.disable`
- `device.revoke`
- `policy.update`
- `audit.read`
- `audit.export`
- `release.approve`
- `tool.approve`

## Papéis padrão

| Papel | Permissões principais |
|---|---|
| Owner | Todas |
| Admin | Todas exceto transferir ownership e recovery sem dupla aprovação |
| Manager | CRUD inventário, aprovar, usar credenciais, auditoria limitada |
| Operator | Ler inventário e abrir sessões autorizadas |
| MikroTikOperator | Abrir WinBox/SSH/API conforme política do grupo |
| NDeskOperator | Criar tickets e operar NDesk conforme política |
| NDeskAdminOperator | Solicitar modo administrador NDesk, sujeito a consentimento do usuário atendido |
| Auditor | Ler auditoria e inventário sem segredos |
| ReadOnly | Ler inventário |
| ReleaseManager | Aprovar versão, changelog e artefatos internos |

## Políticas por grupo

Cada grupo pode sobrescrever:

- Protocolos permitidos.
- Credencial padrão.
- Telnet permitido.
- RDP clipboard/drive.
- WinBox externo permitido.
- Senha via argumento WinBox permitida.
- NDesk permitido.
- NDesk modo administrador permitido.
- Transferência de arquivo NDesk permitida.
- Aprovação obrigatória.
- Cache offline permitido.

## Aprovação gerencial

Mudanças que podem exigir aprovação:

- Criar/rotacionar credencial de grupo.
- Permitir Telnet.
- Permitir senha via argumento WinBox.
- Habilitar exportação.
- Habilitar revelar senha.
- Alterar política NDesk.
- Permitir NDesk administrador.
- Alterar permissão de usuário.
- Aprovar novo binário WinBox empacotado.
- Aprovar novo agente NDesk.

## Avaliação de permissão

Ordem sugerida:

1. Usuário ativo?
2. Dispositivo autorizado?
3. Workspace ativo?
4. Role concede permissão?
5. Grupo/asset nega explicitamente?
6. Política exige aprovação?
7. Ação envolve segredo, ferramenta externa ou acesso remoto?
8. Horário/IP/localização permitidos, se configurado?

Negação explícita deve vencer permissão herdada.

## Consentimento do usuário atendido no NDesk

RBAC interno do operador não substitui consentimento do usuário atendido. Mesmo que o operador tenha `session.ndesk.adminRequest`, a sessão só pode elevar se o usuário atendido conceder permissão e o Windows permitir.
