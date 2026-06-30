# AGENTS.md — instruções para Codex, Antigravity e outros agentes

Este repositório é multiagente. Cada agente deve respeitar fronteiras de módulo, contratos compartilhados, segurança, changelog e versionamento.

## Regras gerais

- Não implemente acesso remoto sem consentimento explícito.
- Não gere código para ocultar processos, burlar segurança, capturar credenciais ou manter persistência sem instalação autorizada.
- Não salve segredos em texto puro.
- Não edite arquivos de outro módulo sem justificar na PR.
- Não altere contratos públicos sem atualizar `contracts/`, `docs/17-contratos-api.md` e criar ADR.
- Toda mudança visível deve atualizar `CHANGELOG.md`.
- Todas as PRs devem ter testes ou justificativa clara.

## Fronteiras de pasta sugeridas

- `src/RemoteOps.Desktop/`: shell Windows, MVVM, navegação, abas, UI.
- `src/RemoteOps.Terminal/`: SSH/Telnet, terminal stream, xterm bridge.
- `src/RemoteOps.Rdp/`: RDP ActiveX/FreeRDP adapters.
- `src/RemoteOps.MikroTik/`: WinBox Runner, RouterOS API/REST/SSH helpers.
- `src/RemoteOps.Sync/`: cliente de sincronização e local outbox.
- `src/RemoteOps.Cloud/`: backend ASP.NET Core.
- `src/RemoteOps.Security/`: criptografia, DPAPI, credential vault.
- `src/RemoteOps.NDesk.Viewer/`: viewer do operador integrado ao Desktop.
- `src/RemoteOps.NDesk.Agent/`: agente temporário Win32/C++ para máquinas atendidas.
- `src/RemoteOps.NDesk.Relay/`: relay/TURN/media relay.
- `tests/`: testes unitários, integração e e2e.
- `contracts/`: contratos JSON versionados.
- `tools/winbox/`: manifesto e binário aprovado do WinBox, se empacotado internamente.
- `docs/`: documentação viva.
- `adr/`: decisões arquiteturais.

## Estratégia de agentes

Use agentes em paralelo somente quando as interfaces estiverem claras. Primeiro estabilize contratos e skeleton; depois distribua módulos.

Cada agente deve retornar:

1. Arquivos alterados.
2. Decisões tomadas.
3. Riscos pendentes.
4. Testes executados.
5. Impacto no changelog/versionamento.
6. Próximo passo recomendado.

## Agentes principais

- `remoteops-architect`: arquitetura e ADRs.
- `desktop-shell-agent`: UI principal.
- `ssh-telnet-agent`: SSH/Telnet.
- `mikrotik-agent`: WinBox Runner e RouterOS.
- `rdp-agent`: Terminal Server/RDP.
- `cloud-sync-agent`: sync e backend.
- `security-agent`: credenciais, RBAC, threat model, NDesk e WinBox Runner.
- `ndesk-agent`: acesso remoto consentido, agente temporário, relay e performance.
- `qa-agent`: testes e laboratório.
- `devops-agent`: CI, assinatura, empacotamento.
- `release-manager-agent`: changelog, SemVer, tags e release notes.
- `research-agent`: spikes técnicos.
- `docs-agent`: documentação operacional.

## Regras específicas: MikroTik WinBox

- Não reimplementar protocolo WinBox proprietário no MVP.
- Abrir `winbox.exe` oficial externo via runner.
- Senha por argumento somente se política permitir.
- Não logar linha de comando completa.
- Validar hash do executável empacotado quando aplicável.

## Regras específicas: NDesk

- Agente temporário deve ser visível e consentido.
- O usuário atendido escolhe permissões.
- Modo administrador exige consentimento separado.
- Sem Java, WebView2 ou .NET moderno no agente temporário legado.
- Windows 7 é modo legado: testar e documentar limitações.
- NAT/CGNAT deve ser tratado com relay próprio e fallback TCP/TLS 443.

## Comandos esperados quando existirem projetos

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
dotnet format --verify-no-changes
```

Para módulos JS do terminal:

```powershell
npm ci
npm run lint
npm test
npm run build
```

Para agente nativo C++ do NDesk, quando existir:

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release
ctest --test-dir build -C Release
```

Para worker Rust, se adotado:

```powershell
cargo fmt --check
cargo clippy -- -D warnings
cargo test
```

Para relay Go, se adotado:

```powershell
go test ./...
go vet ./...
```
