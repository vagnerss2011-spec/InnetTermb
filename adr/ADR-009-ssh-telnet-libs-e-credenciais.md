# ADR-009 — SSH/Telnet: libs externas, layout e higiene de credenciais

## Status

Aceita. Implementada em `feature/terminal-ssh-telnet-v2`.

## Contexto

A frente `feature/terminal-ssh-telnet-v2` re-integra o trabalho do PR #13 (que havia criado
estrutura própria incompatível) na arquitetura canônica do RemoteOps Suite. As decisões abaixo
cobrem: (1) escolha e justificativa das libs externas SSH e Telnet; (2) layout de projetos adotado;
(3) mitigação para a limitação de `string` ao usar Renci.SshNet; (4) modelo TOFU assíncrono;
(5) modelo de consentimento Telnet.

---

## Decisão 1 — Layout de projetos (Tarefa 2)

**Escolhido: Opção A** — consolidar SSH e Telnet em `src/RemoteOps.Terminal` (csproj já existente,
já no root `RemoteOps.sln`, já referenciando `RemoteOps.Contracts` canônico).

**Motivo:** menor diff, PR mais revisável, namespace `RemoteOps.Terminal.Ssh` e
`RemoteOps.Terminal.Telnet` separam a implementação sem precisar de projetos adicionais. A Opção B
(split em `.SSH`/`.Telnet`/`.Core`) só seria preferível se as implementações crescessem a ponto de
conflitar em builds paralelos, o que não é o caso no MVP.

**Consequência:** `RemoteOps.Terminal.csproj` referencia agora também `RemoteOps.Security` (para
`IVault`/`VaultSecret`). Essa dependência é intencional: o módulo Terminal lida diretamente com
credenciais sensíveis e precisa do contrato rico de higiene de memória.

---

## Decisão 2 — Biblioteca SSH: `SSH.NET` (Renci.SshNet)

**Pacote NuGet:** `SSH.NET` versão `2024.2.0`.  
**Namespace de uso:** `Renci.SshNet`.

**Justificativa:**
- Projeto mais maduro de SSH puro-C# (.NET); sem dependências nativas.
- Licença MIT; manutenção ativa (migrou para o pacote `SSH.NET` em 2024.x).
- Suporta autenticação por senha e chave privada, shell interativo (`ShellStream`),
  callback de host key (`HostKeyReceived`), keepalive e resize de PTY
  (`ShellStream.SendWindowChangeRequest`).

**Alternativas descartadas:**
- `WinSCP`: biblioteca COM/interop, mais pesada, não puro-.NET.
- `SSH.NET` fork anterior (`Renci.SshNet` < 2024): mesma lib, só atualização de pacote.
- Implementação própria: inviável para MVP; protocolo SSH é complexo e auditoria difícil.

---

## Decisão 3 — Telnet: implementação própria com TcpClient (sem lib externa)

Telnet (RFC 854/855) é suficientemente simples para implementação própria no MVP:
- `TcpClient` + `NetworkStream` para transporte.
- `TelnetNegotiator` (state machine interna) para IAC parsing e opções ECHO/SGA/NAWS.
- Sem lib externa → sem ADR adicional de dependência.

**Telnet desabilitado por padrão** (política no `ITelnetConsentProvider`). Ver §FIX-2.

---

## FIX-1 — TOFU host key genuinamente assíncrono

**Problema:** O callback `SshClient.HostKeyReceived` é síncrono. Chamar
`.GetAwaiter().GetResult()` dentro dele para aguardar confirmação de UI provoca deadlock
no contexto de WebView2 (UI thread necessária para completar a Task bloqueada é a mesma
que o `.GetResult()` está esperando).

**Solução adotada:**
1. No callback (`HostKeyValidator`), apenas captura a fingerprint e retorna `false`
   (rejeita provisoriamente) — nenhuma operação assíncrona.
2. O `Connect()` lança exceção detectada pelo `catch when (rejectionReason.HasValue)`.
3. **Fora do callback**, aguarda `IHostKeyConfirmation.ConfirmAsync(...)` genuinamente
   assíncrono (implementado pelo Desktop via `TaskCompletionSource<bool>` resolvido pela UI).
4. Se confirmado, adiciona ao `HostKeyStore` e **reconecta** com key já confiada.

**Consequência:** duas conexões TCP em caso de key nova/alterada. Aceitável: ocorre uma
vez por host, amortizado em sessões longas.

---

## FIX-2 — Consentimento Telnet bloqueante

Telnet transmite tudo em texto puro. Antes de qualquer `ConnectAsync()`, o
`TelnetSessionProvider` chama `ITelnetConsentProvider.RequestConsentAsync(host, port, ct)`.
Se o resultado for `false`, a conexão TCP **nunca é aberta**. O provider lança
`InvalidOperationException`. Uso auditado via `ITerminalAuditSink`.

**Grupos autorizados:** controlado pela implementação de `ITelnetConsentProvider` no Desktop
(consulta RBAC/política). Telnet desabilitado por padrão.

---

## FIX-3 — Higiene de senha: limitação Renci.SshNet + mitigação

**Limitação:** `PasswordAuthenticationMethod(username, password)` de Renci.SshNet aceita
apenas `string`. Não é possível passar `ReadOnlyMemory<char>` ou zerar a string após uso
(strings .NET são imutáveis na heap gerenciada).

**O que fazemos:**
1. `IVault.RetrieveAsync(envelopeId, ctx, ct)` → `VaultSecret` (buffer UTF-8 zerado no `Dispose`).
2. `secret.RevealString()` é chamado **uma única vez**, imediatamente antes de `_factory.Create(...)`.
3. O `VaultSecret` é descartado pelo `using` ao final de `OpenAsync`, zerando o buffer UTF-8.
4. A `string` `password` persiste no `PasswordAuthenticationMethod` interno de Renci.SshNet
   até o GC coletar o objeto. **Não há como zerar.**

**Mitigação aceitável:**
- Tempo de vida da `string` é curto (autenticação → GC após `CloseAsync`).
- Nenhuma cópia intermediária em variáveis adicionais.
- Nenhum log da `string`; `TerminalAuditEvent` nunca contém campos de senha.
- `VaultSecret.RevealString()` está documentado como "para fronteiras que exigem string".

**Issue de longo prazo:** contribuir upstream para Renci.SshNet um método `AuthenticateWithSpan`
que aceite `ReadOnlySpan<char>` e nunca materialize `string`. Até lá, a mitigação acima é a
melhor disponível sem fork da lib.

---

## FIX-4 — app.manifest

Nenhum dos projetos do módulo Terminal (`RemoteOps.Terminal`, net10.0 cross-platform)
referencia `app.manifest`. A referência ausente que quebrava o build do PR #13 existia em
projetos próprios criados fora da estrutura canônica, que não foram trazidos para esta PR.

---

## FIX-5 — Auditoria de host key alterada

Quando `HostKeyStore.HasAnyKey(host)` é `true` mas a fingerprint recebida difere da guardada,
o `SshSessionProvider` emite `TerminalAuditEvent { Action = "terminal.hostkey.changed" }` com
a nova fingerprint **antes** de perguntar ao usuário. A fingerprint SHA-256 em hex não é um
segredo; identificadores de chave pública são inócuos em logs de auditoria.

---

## FIX-6 — `"default-group"` hardcoded removido

O valor `"default-group"` do PR #13 foi removido. O grupo real virá do contexto de sessão/RBAC.
Rastreado em: **TODO — abrir issue após PR mesclado** para conectar `ITerminalSecurityContext`
ao grupo RBAC do usuário autenticado.

> ⚠️ **Pendente:** criar a issue de rastreamento e referenciar aqui com o número.

---

## Consequências

- `RemoteOps.Terminal` ganha dependência em `RemoteOps.Security` (aceitável; ADR-003 cobre o vault).
- Dois connects TCP em caso de key nova (TOFU) — aceitável.
- A `string` de senha persiste brevemente em memória gerenciada além do `Dispose` — documentado,
  sem alternativa na API atual de Renci.SshNet.
- Telnet MVP sem autenticação estruturada (credenciais emitidas via protocolo raw, sem cofre):
  uso limitado a equipamentos legados específicos, com consentimento e auditoria.
