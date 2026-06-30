# Versionamento e release

## Esquema

Usar SemVer com sufixos internos:

- `0.x.y-alpha.n`: build interna experimental.
- `0.x.y-beta.n`: build para operadores selecionados.
- `0.x.y-rc.n`: candidata a release.
- `0.x.y`: release estável interna.

Antes de `1.0.0`, qualquer contrato ainda pode mudar, mas mudanças devem ser documentadas em ADR e changelog.

## Componentes versionados

- `RemoteOps Desktop`: cliente principal instalado nos computadores da empresa.
- `RemoteOps Cloud`: backend de sync, RBAC, auditoria e NDesk broker.
- `NDesk Temporary Agent`: agente baixado por link temporário.
- `NDesk Relay`: relay/TURN/media relay.
- `WinBox Tool Bundle`: versão aprovada do WinBox empacotado ou referenciado.

## Compatibilidade

Cada release deve declarar:

- versão mínima do backend;
- versão mínima do cliente desktop;
- versão mínima e máxima do agente NDesk;
- versão do contrato de sync;
- versão do contrato de NDesk;
- compatibilidade Windows 10/11;
- compatibilidade legada Windows 7, quando aplicável.

## Regras de changelog

Toda PR que muda comportamento visível deve atualizar `CHANGELOG.md` em uma seção `Unreleased` ou seção de versão em preparação.

Categorias:

- `Adicionado`
- `Alterado`
- `Corrigido`
- `Removido`
- `Segurança`
- `Migração`
- `Observabilidade`

## Tags

Formato:

```text
remoteops-desktop/v0.3.0
remoteops-cloud/v0.3.0
ndesk-agent/v0.2.0-beta.1
ndesk-relay/v0.2.0
```

## Builds reproduzíveis

Cada artefato de release deve registrar:

- commit SHA;
- branch;
- timestamp UTC;
- versão do SDK/toolchain;
- hash SHA-256 do artefato;
- assinatura do binário quando aplicável;
- changelog da versão.
