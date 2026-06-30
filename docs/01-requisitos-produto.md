# 01 — Requisitos de produto

## Personas

### Operador NOC/Suporte

- Pesquisa hosts rapidamente.
- Abre múltiplos SSH, Telnet e RDP em abas.
- Usa credenciais de grupo sem precisar saber a senha em texto puro.
- Envia link de assistência remota com expiração.

### Gerente/Administrador

- Cria grupos, usuários e permissões.
- Aprova alterações sensíveis.
- Audita quem acessou o quê, quando e por qual protocolo.
- Rotaciona credenciais de grupo.

### Auditor/Security

- Consulta logs sem ver segredos.
- Verifica mudanças de credenciais, acessos remotos e exportações.
- Confirma conformidade com políticas internas.

### Cliente/Usuário assistido

- Baixa um agente temporário.
- Visualiza claramente quem está conectado.
- Autoriza controle, visualização, transferência de arquivo ou elevação separadamente.
- Encerra a sessão a qualquer momento.

## Requisitos funcionais

### Gestão de inventário

- Criar grupos hierárquicos.
- Criar hosts com nome, IP/FQDN, IPv4, IPv6, tags e observações.
- Associar um ou mais endpoints por host: SSH, Telnet, RDP, RouterOS API, Web, custom.
- Importar e exportar inventário em formato controlado.
- Buscar por nome, IP, tag, protocolo, fabricante, localização e observação.

### Credenciais

- Criar credenciais individuais.
- Criar grupos de senha.
- Herdar credencial por grupo, com override por host.
- Suportar senha, chave privada SSH, senha da chave, usuário, domínio Windows e MFA metadata.
- Impedir exibição de senha por padrão.
- Rotacionar credenciais.
- Auditar uso e alteração.

### Sessões SSH/Telnet

- Abrir sessão em aba.
- Múltiplas abas simultâneas.
- Reconexão manual.
- Perfis de terminal: encoding, tamanho, tema, keepalive.
- Envio opcional de comandos iniciais seguros.
- Templates por fornecedor.

### MikroTik

- Abrir terminal SSH.
- Consultar informações via RouterOS API-SSL/REST quando configurado.
- Criar uma UI simplificada semelhante ao objetivo do WinBox, sem depender do protocolo WinBox proprietário.
- Mapear serviços, interfaces, rotas, logs e recursos comuns por API.

### RDP

- Abrir RDP em aba ou janela destacável.
- Suportar porta customizada.
- Suportar NLA e domínio.
- Controlar redirecionamento de disco, clipboard, impressora e áudio por política.
- Validar certificado ou registrar exceção auditada.

### NDesk

- Gerar convite/link temporário.
- Baixar agente temporário assinado.
- Exigir consentimento explícito.
- Mostrar banner permanente durante a sessão.
- Permitir visualização somente ou controle.
- Permitir revogação imediata.
- Auditar início, fim, operador, permissões e IPs.

### Sincronização

- Sincronizar inventário, grupos, permissões e metadados.
- Sincronizar segredos criptografados.
- Funcionar offline com outbox local.
- Resolver conflitos de forma previsível.
- Atualizar outros clientes quase em tempo real.

## Requisitos não funcionais

- Windows 10/11 como alvo inicial.
- Suporte IPv4 e IPv6, preferindo IPv6 quando disponível.
- Baixa latência ao abrir sessões.
- Segurança por padrão.
- Logs sem segredos.
- Instalador assinado.
- Modo de recuperação para troca de máquina/perda de perfil.
- Arquitetura modular e testável.
- CI executando em runner Windows.

## Critérios de aceite do MVP

- Usuário cria grupo, host e credencial.
- Usuário abre SSH em aba usando credencial salva.
- Usuário abre RDP em aba usando credencial salva.
- Outro usuário vê o host sincronizado após login.
- Alterações geram audit log.
- Credenciais não aparecem em texto puro no banco local nem no servidor.
- Build e testes passam no GitHub Actions.
