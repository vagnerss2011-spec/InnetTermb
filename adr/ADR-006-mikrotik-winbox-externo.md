# ADR-006 — MikroTik via WinBox oficial externo no MVP

## Status

Aceita para o MVP — atualizada com decisões da re-integração (frente `feature/mikrotik-winbox-v2`).

## Contexto

A equipe usa MikroTik intensamente e o WinBox é a experiência operacional mais conhecida. Reimplementar o protocolo WinBox adicionaria risco, escopo e manutenção. A documentação oficial do WinBox permite chamada por linha de comando com destino, usuário, senha e workspace/sessão, além de formato para IPv6.

## Decisão

No MVP, o RemoteOps abrirá o `winbox.exe` oficial externo por meio de um `WinBoxRunner` controlado. O RemoteOps continuará sendo a fonte de verdade para grupos, hosts, permissões, credenciais, auditoria e sincronização.

## Implementação atual (`feature/mikrotik-winbox-v2`)

- `WinBoxRunner : IWinBoxRunner` — implementa `LaunchAsync(ExternalToolLaunchRequest, CancellationToken)`.
- `WinBoxArgumentBuilder` — monta argumentos posicionais com `ArgumentList` (sem interpolação de string).
- `WinBoxToolManifest` — valida SHA-256 do executável antes de executar; fail-closed se sha256 ausente/inválido.
- `LocalWinBoxPolicyProvider` — avalia política por workspace/host; senha via argumento desativada por padrão (Modo A).
- `IWinBoxAuditSink` — emite eventos sem segredo; nunca loga linha de comando completa.

## Decisões de argumentos posicionais

### Workspace como 4º posicional — DEFERIDO

A documentação interna (`docs/21`) mostra o formato conceitual `winbox.exe <connect-to> <login> <password> <workspace>`. Essa sintaxe não foi validada contra a CLI oficial do WinBox neste sprint.

**Decisão**: `WorkspaceName` está no contrato `ExternalToolLaunchRequest` mas o runner NÃO emite workspace como argumento posicional nesta versão. Comportamento não documentado é proibido pela política de segurança (ADR-006 controles). Revisar quando a CLI oficial for consultada.

### RoMON — DEFERIDO (campo mantido no contrato, execução recusada)

A sintaxe `--romon <agent> <connect-to> ...` mostrada nos docs internos é conceitual e não foi confirmada contra a CLI oficial do WinBox. Emitir argumentos não confirmados poderia corromper a linha de comando posicional.

**Decisão**: O campo `ExternalToolRomon` permanece no contrato `ExternalToolLaunchRequest` (sem mudança de schema) mas o `WinBoxRunner` recusa `Romon.Enabled = true` com `WinBoxValidationException` auditada (`winbox_open_failed`, `reason=romon_not_confirmed_official_cli`). Implementar quando a sintaxe oficial for confirmada.

## Risco: senha via argumento de processo

**A senha passada via argumento de processo é visível na tabela de processos local** (e.g., Task Manager, `tasklist`, EDR, logs de processo). Qualquer processo com permissão de leitura de processos pode ver os argumentos.

Controles implementados:
- Senha desativada por padrão (`PasswordArgumentAllowed = false` em `WinBoxPolicyConfig`).
- `IncludePasswordArgument = true` no request + `PasswordArgumentAllowed = true` na política + credencial resolvida → todos os três devem ser verdadeiros para incluir senha.
- Evento `winbox_password_argument_used` emitido (sem a senha).
- Linha de comando completa nunca logada.
- Administradores devem ser avisados visualmente quando o Modo B estiver ativo.

**Sign-off do security-agent**: pendente — PR deve ter label `security-reviewed` antes do merge (gate de CI).

## Consequências positivas

- Reduz muito o escopo do módulo MikroTik.
- Entrega valor rápido para operadores acostumados ao WinBox.
- Evita engenharia reversa de protocolo proprietário.
- Permite focar em sync, RBAC, SSH, RDP e governança.

## Consequências negativas

- A UI do WinBox fica fora do controle visual do RemoteOps.
- Senha via argumento de processo tem risco de exposição local (documentado acima).
- Dependemos da compatibilidade de parâmetros do WinBox.
- Atualizações do WinBox precisam ser validadas (SHA-256 deve ser atualizado no manifesto).

## Controles

- Validar hash SHA-256 do executável empacotado; fail-closed se ausente/inválido.
- Não logar argumentos sensíveis nem linha de comando completa.
- Senha automática desativada por padrão (Modo A).
- Política por workspace/grupo para permitir senha via argumento (Modo B).
- Auditoria de toda abertura, falha e uso de senha.
- RouterOS API-SSL/REST permanece no roadmap para UI própria futura.

## Critério de revisão futura

Revisar esta ADR quando:

- a equipe precisar de UI MikroTik própria;
- o WinBox mudar comportamento de linha de comando;
- a política de segurança proibir senha por argumento;
- API-SSL/REST cobrir a maioria das operações necessárias;
- a sintaxe oficial de RoMON e workspace posicional for confirmada.
