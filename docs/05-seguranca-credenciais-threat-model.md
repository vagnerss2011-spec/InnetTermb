# 05 — Segurança, credenciais e threat model

## Princípio central

O sistema guarda chaves que dão acesso administrativo à infraestrutura. A arquitetura deve assumir que laptops podem ser perdidos, tokens podem ser roubados e operadores podem ter permissões diferentes.

## Objetivos de segurança

- Nenhuma senha em texto puro no banco local, servidor, logs ou analytics.
- Credenciais descriptografadas somente em memória e pelo menor tempo possível.
- Autenticação forte para usuários.
- RBAC por workspace, grupo, host, protocolo e ação.
- Auditoria completa de ações sensíveis.
- Revogação rápida de usuários e dispositivos.
- Proteção contra alteração maliciosa do cliente e supply chain.

## Modelo de criptografia recomendado

### Envelope encryption

1. Cada workspace possui uma `Workspace Data Key`.
2. Cada segredo é criptografado com uma chave de conteúdo derivada/rotacionável.
3. A chave do workspace é distribuída aos usuários autorizados como envelope criptografado para aquele usuário/dispositivo.
4. No Windows, a cópia local da chave é protegida com DPAPI no escopo do usuário ou dispositivo, conforme política.
5. O servidor armazena ciphertext e metadados, mas não precisa conhecer plaintext.

## Estratégia de recuperação

E2EE puro pode causar perda definitiva em caso de perda de chaves. Para ambiente empresarial, prever:

- Chave de recuperação offline guardada por dois administradores.
- Política de escrow opcional.
- Rotação obrigatória após desligamento de funcionário.
- Registro de quem recuperou e quando.

## Autenticação

Opções:

- OIDC com provedor corporativo, preferível.
- Usuário/senha + MFA no MVP se não houver IdP.
- Device registration para máquinas confiáveis.
- Sessões com refresh token rotacionado.
- Revogação por usuário, dispositivo e workspace.

## RBAC inicial

| Papel | Permissões |
|---|---|
| Owner | Tudo, inclusive políticas de segurança |
| Admin | Usuários, grupos, hosts, credenciais, auditoria |
| Manager | Aprovar mudanças, criar grupos e hosts |
| Operator | Abrir sessões autorizadas, criar tickets NDesk |
| ReadOnly | Ver inventário sem abrir sessão |
| Auditor | Ver auditoria e relatórios, sem segredos |

## Ações auditáveis

- Login/logout.
- Dispositivo registrado/revogado.
- Host criado/alterado/excluído.
- Credencial criada/alterada/rotacionada/usada.
- Sessão SSH/Telnet/RDP iniciada/finalizada.
- NDesk convite criado/expirado/usado.
- NDesk controle autorizado/revogado.
- Export/import.
- Alteração de permissão.
- Falha de autenticação.

## Threat model resumido

| Ameaça | Controle |
|---|---|
| Roubo de notebook | DB local criptografado, DPAPI, expiração de cache, revogação de dispositivo |
| Operador malicioso | RBAC mínimo, auditoria, aprovação gerencial, gravação opcional de sessão NDesk |
| Vazamento em log | Sanitização obrigatória, testes de redaction |
| MITM em sync | TLS, pinning opcional, assinatura de payload sensível |
| MITM em SSH/RDP | Host key/cert validation, exceções auditadas |
| Compromisso do servidor | Segredos E2EE/envelope, separação de chaves, hardening |
| Supply chain | Dependabot, lockfiles, assinatura de build, branch protection |
| Acesso remoto abusivo | Consentimento visível, expiração, revoke button, sem modo oculto |
| Conflito de credencial | Rotação versionada, sem merge automático de segredo |
| Exfiltração via export | Permissão específica, export criptografado, watermark/auditoria |

## Políticas do módulo NDesk

Obrigatórias:

- O usuário assistido precisa ver que a sessão está ativa.
- O usuário assistido precisa poder encerrar a sessão a qualquer momento.
- Controle remoto precisa de permissão separada de visualização.
- Transferência de arquivo precisa de permissão separada.
- O agente temporário deve expirar.
- O binário deve ser assinado.
- Não implementar modo stealth.

## Proteção de memória

- Usar `SecureString` não resolve todos os problemas em .NET moderno; tratar como redução parcial, não garantia.
- Minimizar lifetime de strings com senha.
- Preferir APIs que aceitem credenciais sem persistir globalmente.
- Limpar buffers quando possível.
- Nunca serializar request com segredo em log.

## Checklist de PR sensível

- [ ] Alterei código de credencial, criptografia, autenticação ou sessão remota.
- [ ] Não há segredo em log.
- [ ] Há testes cobrindo erro e sucesso.
- [ ] Há auditoria.
- [ ] Há documentação/ADR.
- [ ] A UX deixa claro o impacto para o usuário.

## Política WinBox Runner

O WinBox oficial externo facilita o MVP MikroTik, mas cria uma decisão de segurança específica: passar senha por argumento de processo pode expor a senha para ferramentas locais de diagnóstico, EDR, dumps ou logs mal configurados.

Política padrão recomendada:

- Por padrão, abrir WinBox com host/porta/usuário e senha vazia.
- Permitir senha via argumento somente quando o grupo/tenant aprovar explicitamente.
- Nunca logar linha de comando completa.
- Nunca salvar senha no `winbox.cfg` como fonte de verdade do RemoteOps.
- Validar hash do `winbox.exe` empacotado.
- Auditar uso de senha automática sem registrar a senha.

## Política NDesk administrador

O modo administrador deve ser tratado como ação sensível:

- exige permissão separada do usuário atendido;
- exige registro em auditoria;
- deve respeitar UAC;
- não deve capturar nem mascarar tela de credenciais para enganar usuário;
- não deve tentar burlar Secure Desktop;
- helper temporário precisa ser assinado, visível e removido ao final;
- qualquer modo persistente precisa ser instalação explícita, governada e auditada.

## Política Windows legado

Windows 7 é legado e sem suporte moderno de segurança. O projeto pode oferecer agente temporário por necessidade operacional, mas deve aplicar controles extras:

- link temporário curto;
- binário assinado;
- TLS obrigatório;
- sem persistência no MVP;
- sem armazenar credencial local;
- modo administrador desabilitado por padrão;
- telemetria mínima para troubleshooting, sem conteúdo de tela;
- aviso de risco para operadores internos.
