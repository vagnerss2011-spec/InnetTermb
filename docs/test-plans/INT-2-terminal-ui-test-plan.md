# Plano de Testes — INT-2: Aba de terminal viva (WebView2 + xterm.js)

Data: 2026-06-30  
Branch: `feature/integration-terminal-ui`  
Responsável: ssh-telnet-agent

---

## 1. Escopo

Cobre os componentes criados ou modificados pela frente INT-2:

| Componente | Tipo de teste |
|---|---|
| `TerminalTabViewModel` | Unitário (xUnit) |
| `TabsViewModel` (integração com terminal) | Unitário (xUnit) |
| `TerminalTabView` (WebView2) | Manual |
| `WpfHostKeyConfirmationDialog` | Manual |
| `WpfTelnetConsentDialog` | Manual |
| `DebugTerminalAuditSink` | Unitário (xUnit — indireto via InMemoryTerminalAuditSink) |
| Bridge JS↔C# (`index.html`) | Manual + revisão estática |

---

## 2. Testes automatizados

### 2.1 TerminalTabViewModelTests

Arquivo: `tests/RemoteOps.UnitTests/Desktop/TerminalTabViewModelTests.cs`

| # | Teste | O que verifica |
|---|---|---|
| 1 | `ConnectAsync_OpensSession_AndTransitionsToConnected` | Estado muda idle→connected após OpenAsync |
| 2 | `ConnectAsync_PassesTerminalOptions_ToProvider` | Cols/Rows chegam ao provider |
| 3 | `ConnectAsync_WhenAlreadyConnecting_SecondCallIsIgnored` | Interlocked CAS impede dupla conexão |
| 4 | `ConnectAsync_WhenProviderThrows_ResetsToIdleState` | Exceção em OpenAsync → estado volta a 0 |
| 5 | `SendInputAsync_WhenNotConnected_ReturnsWithoutWrite` | Guard: sem sessão ativa, sem escrita |
| 6 | `SendInputAsync_WhenConnected_ForwardsToProvider` | Bytes chegam ao provider.WriteAsync |
| 7 | `ResizeAsync_WhenNotConnected_ReturnsWithoutCall` | Guard: sem sessão ativa, sem resize |
| 8 | `ResizeAsync_WhenConnected_ForwardsColsAndRows` | Dimensões chegam ao provider.ResizeAsync |
| 9 | `OutputReceived_IsFired_ForEachChunkFromProvider` | Pump dispara evento por chunk |
| 10 | `PumpEnd_ResetsIsConnected_ToFalse` | Fim do stream → IsConnected = false |
| 11 | `CloseAsync_WhenNotConnected_IsNoop` | Não lança, CloseCount = 0 |
| 12 | `CloseAsync_CancelsSessionAndCallsProviderClose` | CloseAsync chama provider.CloseAsync |
| 13 | `CloseAsync_CalledTwice_IsIdempotent` | Segunda chamada não duplica CloseAsync |
| 14 | `Title_AndProtocol_AreAccessibleFromBaseClass` | Herança de SessionTabViewModel correta |

### 2.2 TabsViewModelTerminalTests

Arquivo: `tests/RemoteOps.UnitTests/Desktop/TabsViewModelTerminalTests.cs`

| # | Teste | O que verifica |
|---|---|---|
| 1 | `OpenTerminalTab_AddsAndActivates` | Aba terminal adicionada e ativada |
| 2 | `OpenTerminalTab_MultipleTabs_LastIsActive` | Segunda aba fica ativa |
| 3 | `CloseTab_TerminalTab_CallsCloseAsyncAndRemovesTab` | Aba removida da coleção |
| 4 | `CloseTab_PinnedTerminalTab_CannotExecute` | Aba fixada não pode ser fechada |
| 5 | `CloseTerminalTab_ActivatesPreviousTab` | Tab anterior ativada ao fechar |
| 6 | `MixedTabs_TerminalAndPlaceholder_CoexistCorrectly` | Terminal e placeholder coexistem |

---

## 3. Testes negativos

| Cenário | Mecanismo de proteção | Teste |
|---|---|---|
| Dupla conexão concorrente | `Interlocked.CompareExchange` em `_connectionState` | Teste #3 (unitário) |
| Tab fechada antes de WebView2 inicializar | `if (!IsLoaded) return;` em `InitWebViewAsync` | Manual (fechar aba em < 200 ms) |
| Provider lança em `OpenAsync` | `catch { ... Exchange(0); throw; }` | Teste #4 (unitário) |
| Input malformado no bridge JS→C# | `try/catch` em `OnWebMessageReceived` | Revisão estática + manual |
| Terminal output injetado como HTML | `term.write(Uint8Array)` — não `innerHTML` | Revisão estática |
| Credenciais nos logs de auditoria | `TerminalAuditEvent` sem campo de senha/conteúdo | Revisão estática do contrato |
| CallStack overflow em pastes grandes | Loop explícito em vez de `apply(null, bytes)` | Revisão estática + manual (paste 100 KB) |
| CloseAsync chamado duas vezes | Guard `if (_handle == null) return` | Teste #13 (unitário) |

---

## 4. Checklist de segurança (obrigatório pelo CLAUDE.md)

- [x] Logs não contêm senha, chave privada ou conteúdo de terminal.
  - `DebugTerminalAuditSink` loga apenas `action/sessionId/host/userId`.
  - `TerminalAuditEvent` não possui campo para conteúdo PTY ou credenciais.
- [x] Permissões negadas são testadas.
  - Aba fixada: `CanExecute = false` (teste #4).
  - Sessão não conectada: input/resize ignorados sem erro (testes #5, #7).
- [x] Erros não expõem stack/segredo ao usuário final.
  - `OnNavigationCompleted` falha mostram apenas mensagem genérica.
  - `OnWebMessageReceived` captura `Exception` e escreve apenas no `Debug.WriteLine`.
- [x] Ações sensíveis geram auditoria.
  - Os provedores `SshSessionProvider` e `TelnetSessionProvider` emitem `TerminalAuditEvent` em `OpenAsync` e `CloseAsync` (verificado em `SshSessionProviderTests`).
- [x] DevTools desabilitado em Release (`#if !DEBUG`).
- [x] Context menu desabilitado em Release.
- [x] Host objects desabilitados (`AreHostObjectsAllowed = false`).
- [x] CSP bloqueia recursos externos e inline scripts.
- [x] Virtual host local — zero requisições de rede externa.
- [x] TOFU via `TaskCompletionSource` — não bloqueia dispatcher (ADR-009 FIX-1).
- [x] Consentimento Telnet genuinamente bloqueante (ADR-009 FIX-2).

---

## 5. Testes manuais

### Pré-requisito

```powershell
# Build
cd C:\dev\remoteops-int-terminal
dotnet build src/RemoteOps.Desktop/RemoteOps.Desktop.csproj

# Frontend (se assets não estiverem presentes)
cd src\RemoteOps.Desktop\Terminal\wwwroot
npm ci
node build.js
```

### Casos de teste

| ID | Cenário | Passos | Resultado esperado |
|---|---|---|---|
| MT-01 | SSH ao host de lab | 1. Adicionar endpoint SSH no InspectorViewModel. 2. Clicar "Abrir SSH". | Aba abre, banner SSH exibido, prompt interativo. |
| MT-02 | Confirmação TOFU na primeira conexão | Conectar ao host SSH desconhecido. | Dialog TOFU aparece; conexão só prossegue após "Aceitar". |
| MT-03 | Rejeição TOFU | Clicar "Cancelar" no dialog TOFU. | Sessão não é aberta; aba fecha ou mostra erro. |
| MT-04 | Consentimento Telnet | Conectar a endpoint Telnet. | Dialog de alerta de segurança aparece; TCP só conecta após "Aceitar". |
| MT-05 | Rejeição Telnet | Clicar "Cancelar" no consentimento Telnet. | Sessão não aberta. |
| MT-06 | Resize da janela | Redimensionar janela do app. | Terminal reajusta colunas/linhas via FitAddon; PTY remoto responde ao resize. |
| MT-07 | 10 abas simultâneas | Abrir 10 sessões SSH. | Todas ativas sem degradação perceptível; memória não cresce indefinidamente. |
| MT-08 | Fechar aba com sessão ativa | Clicar "×" na aba enquanto sessão está aberta. | Sessão encerrada, processo SSH limpo, sem processo órfão. |
| MT-09 | Fechar aba durante inicialização do WebView2 | Fechar aba < 200 ms após abrir. | Nenhuma sessão SSH é aberta (guard `if (!IsLoaded) return`). |
| MT-10 | Paste de 100 KB | Colar bloco de texto grande no terminal. | Sem trava ou stack overflow; texto aparece no terminal. |
| MT-11 | Switch entre abas | Abrir 3 abas, alternar entre elas. | Sessão SSH continua ativa em todas; WebView2 reinicia mas pump sobrevive. |
| MT-12 | DevTools desabilitados em Release | Build Release, clicar com botão direito na aba terminal. | Context menu não aparece. |
| MT-13 | Logs sem credenciais | Abrir console de Debug (VS), conectar SSH. | Nenhuma senha ou conteúdo de terminal visível nos logs. |

---

## 6. Dados de teste

- Usar host SSH de lab isolado (não produção).
- Credenciais de teste: usuário `admin`, senha gerada por script, nunca comitada.
- Não usar infraestrutura de produção em testes manuais.
- Fixture de endpoint: `127.0.0.1:22` (pode ser mock SSH via OpenSSH local no Windows).

---

## 7. Critérios de aceite (INT-2)

- [ ] Sessão SSH a host de lab abre terminal funcional na aba.
- [ ] Consentimento Telnet aparece e bloqueia conexão até ack.
- [ ] 10 abas simultâneas sem crash ou vazamento visível.
- [ ] `dotnet build` sem erros ou warnings.
- [ ] `dotnet test` passa todos os testes (incluindo os novos de TerminalTabViewModel).
- [ ] `dotnet format --verify-no-changes` sem diff.
- [ ] `npm ci && node build.js` documentado em CONTRIBUTING.md.
- [ ] Label `security-reviewed` adicionado ao PR.
