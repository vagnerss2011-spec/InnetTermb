# ADR-006 — MikroTik via WinBox oficial externo no MVP

## Status

Aceita para o MVP.

## Contexto

A equipe usa MikroTik intensamente e o WinBox é a experiência operacional mais conhecida. Reimplementar o protocolo WinBox adicionaria risco, escopo e manutenção. A documentação oficial do WinBox permite chamada por linha de comando com destino, usuário, senha e workspace/sessão, além de formato para IPv6.

## Decisão

No MVP, o RemoteOps abrirá o `winbox.exe` oficial externo por meio de um `WinBoxRunner` controlado. O RemoteOps continuará sendo a fonte de verdade para grupos, hosts, permissões, credenciais, auditoria e sincronização.

## Consequências positivas

- Reduz muito o escopo do módulo MikroTik.
- Entrega valor rápido para operadores acostumados ao WinBox.
- Evita engenharia reversa de protocolo proprietário.
- Permite focar em sync, RBAC, SSH, RDP e governança.

## Consequências negativas

- A UI do WinBox fica fora do controle visual do RemoteOps.
- Senha via argumento de processo tem risco de exposição local.
- Dependemos da compatibilidade de parâmetros do WinBox.
- Atualizações do WinBox precisam ser validadas.

## Controles

- Validar hash do executável empacotado.
- Não logar argumentos sensíveis.
- Senha automática desativada por padrão.
- Política por tenant/grupo para permitir senha via argumento.
- Auditoria de toda abertura.
- RouterOS API-SSL/REST permanece no roadmap para UI própria futura.

## Critério de revisão futura

Revisar esta ADR quando:

- a equipe precisar de UI MikroTik própria;
- o WinBox mudar comportamento de linha de comando;
- a política de segurança proibir senha por argumento;
- API-SSL/REST cobrir a maioria das operações necessárias.
