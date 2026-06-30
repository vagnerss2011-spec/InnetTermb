# RemoteOps Suite — plano de projeto multiagente

Este pacote organiza o projeto de um sistema interno Windows para gestão centralizada de acessos SSH, Telnet, MikroTik/RouterOS, RDP/Terminal Server e assistência remota consentida no estilo TeamViewer/AnyDesk, com sincronização em nuvem, multiusuário e governança profissional de código.

A versão 2 incorpora três decisões importantes:

1. **MikroTik no MVP via WinBox oficial externo**: o sistema armazena e governa host, porta, usuário, credencial e grupo, mas abre o `winbox.exe` empacotado/validado dentro da pasta do produto. Isso evita reimplementar o protocolo WinBox.
2. **NDesk com agente temporário leve para Windows antigo**: o módulo de assistência remota deve ter um agente Win32 nativo, sem Java, sem WebView2 e sem exigir instalação de .NET moderno na máquina do cliente atendido.
3. **Governança de projeto grande**: versionamento SemVer, changelog obrigatório, CODEOWNERS, PRs pequenos, ADRs e agentes com fronteiras bem definidas.

## Decisão inicial de stack

**Produto principal instalado nos computadores da empresa:**

- **Desktop Windows 10/11:** C#/.NET 10 + WPF + MVVM.
- **Terminal SSH/Telnet:** WebView2 + xterm.js no front; backend C# com adaptadores SSH/Telnet.
- **RDP:** Microsoft Remote Desktop ActiveX/MSTSCAX hospedado por WPF/WinForms interop, com spike paralelo avaliando FreeRDP.
- **MikroTik:** WinBox oficial externo no MVP + RouterOS SSH/API-SSL/REST para recursos estruturados futuros.
- **Backend de sync:** ASP.NET Core + PostgreSQL + SignalR/WebSocket + Redis opcional.
- **Storage local:** SQLite criptografado, idealmente SQLCipher, com outbox/inbox de sincronização.
- **Credenciais:** criptografia por envelope, chave de workspace, DPAPI no cliente Windows, rotação e auditoria.

**Agente temporário de acesso remoto NDesk:**

- **Windows 10/11:** agente nativo com captura moderna, codec adaptativo e transporte WebRTC/relay.
- **Windows 7 SP1 legado:** agente Win32/C++ nativo com runtime estático quando possível, sem Java, sem WebView2 e sem exigir .NET moderno. O suporte a Windows 7 deve ser tratado como modo legado, com matriz de testes e aviso de risco operacional.
- **NAT/conexão lenta:** broker próprio, STUN/TURN/relay, codec adaptativo, redução dinâmica de qualidade, frame rate e resolução, envio por regiões alteradas e telemetria de latência/perda.
- **Permissões:** usuário da ponta escolhe visualização, controle, transferência e modo básico ou administrador. Não deve existir modo oculto, bypass de UAC ou persistência silenciosa.

## Como usar este pacote

1. Crie um repositório privado no GitHub.
2. Copie todos os arquivos deste pacote para a raiz do repositório.
3. Comece por `docs/00-entendimento-escopo.md`, `docs/02-arquitetura-geral.md`, `docs/21-mikrotik-winbox-runner.md`, `docs/22-ndesk-performance-legacy-windows.md` e `docs/23-governanca-versionamento-changelog.md`.
4. Configure Claude Code usando `CLAUDE.md` e os subagentes em `.claude/agents/`.
5. Configure Codex com `AGENTS.md`.
6. Configure GitHub Actions copiando `.github/workflows/ci.yml`.
7. Cada agente deve trabalhar em uma branch própria, seguindo `docs/11-devops-github-ci.md` e os critérios de aceite dos módulos.

## Ordem recomendada de execução

1. Contratos, modelo de dados, CI, versionamento, changelog e esqueleto do repositório.
2. Desktop shell com árvore de grupos, abas, banco local e sync básico.
3. SSH/Telnet com xterm.js e perfis de fornecedor.
4. MikroTik WinBox Runner: cadastro, validação do executável, montagem segura de argumentos e auditoria.
5. Backend de sync, RBAC, auditoria e aprovação de alterações.
6. RDP integrado.
7. NDesk MVP: broker, convite temporário, agente temporário Win32, visualização, controle separado e relay.
8. NDesk robusto: NAT difícil, conexão lenta, telemetria, modo administrador consentido e hardening.
9. Instalador, assinatura, observabilidade, operação e releases internos.

## Arquivos principais

- `docs/00-entendimento-escopo.md`: entendimento do problema e premissas.
- `docs/02-arquitetura-geral.md`: arquitetura do sistema.
- `docs/03-stack-tecnica-adr.md`: comparação de stacks e decisão inicial.
- `docs/05-seguranca-credenciais-threat-model.md`: segurança e modelo de ameaças.
- `docs/07-ssh-telnet-mikrotik.md`: módulo Multi-Putty/Moba-like e MikroTik.
- `docs/08-rdp-terminal-server.md`: módulo RDP.
- `docs/09-acesso-remoto-ndesk.md`: módulo de assistência remota consentida.
- `docs/21-mikrotik-winbox-runner.md`: execução do WinBox oficial externo.
- `docs/22-ndesk-performance-legacy-windows.md`: performance, NAT e Windows antigo no NDesk.
- `docs/23-governanca-versionamento-changelog.md`: changelog, versionamento e governança.
- `docs/10-backend-cloud-sync.md`: backend cloud/sync.
- `docs/12-roadmap-fases.md`: fases de entrega.
- `docs/15-pesquisa-e-spikes.md`: pesquisas técnicas para validar riscos.
- `.claude/agents/*.md`: subagentes para Claude Code.
- `AGENTS.md`: instruções para Codex, Antigravity e agentes genéricos.
- `CHANGELOG.md`: histórico de versões.
- `VERSIONING.md`: política de SemVer, build e release.

## Observação de segurança

Este projeto lida com senhas, chaves privadas, sessões administrativas e controle remoto. Todo desenvolvimento deve priorizar consentimento explícito, autenticação forte, auditoria, criptografia, logs sem segredos, revisão humana e operação transparente. O módulo NDesk deve ser uma ferramenta de suporte autorizada, nunca uma ferramenta de acesso oculto.
