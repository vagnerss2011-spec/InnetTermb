# ADR-008 — Biblioteca SSH: Renci.SshNet (SSH.NET)

## Status

Aceita.

## Contexto

O módulo de terminal SSH precisa de uma biblioteca que implemente o protocolo SSH-2 em C#/.NET, incluindo:
- Autenticação por senha e por chave privada (RSA, ECDSA, Ed25519).
- Shell interativo com pseudoterminal (PTY).
- Resize de PTY via SSH_MSG_CHANNEL_REQUEST `window-change`.
- Keepalive via SSH_MSG_GLOBAL_REQUEST `keepalive@openssh.com`.
- Validação de host key (fingerprint SHA-256).
- Suporte a IPv6.

Alternativas avaliadas:

| Biblioteca        | Licença   | .NET 10 | PTY/Resize | Manutenção |
|-------------------|-----------|---------|------------|------------|
| **Renci.SshNet**  | MIT       | ✅      | ✅         | Ativa      |
| WinSCP .NET       | GPL/LGPL  | ✅      | Limitado   | Ativa, paga comercial |
| libssh2 via P/Invoke | LGPL   | Manual  | Sim        | Complexo   |
| Implementação própria | —     | —       | Sim        | Alto risco |

## Decisão

Usar **Renci.SshNet** (NuGet `SSH.NET`, versão ≥ 2024.x).

Razões:
1. MIT license, sem custo comercial.
2. Suporte nativo a PTY shell com `ShellStream` e resize via `SendPseudoTerminalSizeChange`.
3. Evento `HostKeyReceived` expõe fingerprint antes da conexão — permite fluxo TOFU.
4. Mantida ativamente com suporte a .NET moderno.

## Consequências positivas

- Implementação limpa sem P/Invoke ou interop nativo.
- Fingerprint SHA-256 disponível diretamente no evento.
- Testes unitários possíveis via mock de `ISshClient` (wrapper necessário).

## Consequências negativas

- Não suporta SFTP acelerado via libssh2 (aceito para MVP; pós-MVP pode adicionar).
- Jump host não é suportado nativamente (pós-MVP).
- Necessário wrapper para testabilidade (SSH.NET não tem abstração de interface pública).

## Restrições de uso

- Nunca logar fingerprint de host key em produção sem nível DEBUG explícito.
- Nunca logar conteúdo de stream SSH.
- Credenciais devem ser descartadas via `Dispose()` imediatamente após uso no handshake.
- Qualquer mudança de versão major exige revisão deste ADR.
