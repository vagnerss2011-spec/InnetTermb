# Sprint 04 — MikroTik WinBox Runner

Você é o agente responsável por implementar o MVP de abertura do WinBox oficial externo.

## Leitura obrigatória

- `docs/07-ssh-telnet-mikrotik.md`
- `docs/21-mikrotik-winbox-runner.md`
- `adr/ADR-006-mikrotik-winbox-externo.md`
- `contracts/external-tool-launch.schema.json`
- `docs/05-seguranca-credenciais-threat-model.md`

## Objetivo

Criar o módulo `RemoteOps.MikroTik` com modelos e serviços para montar argumentos, validar políticas e abrir `winbox.exe` sem logar segredos.

## Entregas

- `MikroTikHostProfile`.
- `WinBoxLaunchRequest`.
- `WinBoxArgumentBuilder`.
- `WinBoxRunner`.
- `WinBoxToolManifest`.
- Testes unitários de IPv4, IPv6, porta, senha vazia, senha com espaço, workspace e RoMON.
- Eventos de auditoria sem senha.

## Restrições

- Não logar linha de comando completa.
- Não persistir senha em arquivo do WinBox.
- Senha via argumento somente com política explícita.
- Usar API de argumentos segura, sem concatenação manual.

## Critério de aceite

- Testes passam.
- IPv6 é formatado com colchetes.
- Senha não aparece em logs/test snapshots.
- Falta de permissão bloqueia abertura com senha.
