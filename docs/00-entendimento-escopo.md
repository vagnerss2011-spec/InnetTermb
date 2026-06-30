# 00 — Entendimento, escopo e premissas

## Entendimento do objetivo

O sistema será uma plataforma interna Windows para uma empresa que acessa diariamente servidores Linux, roteadores, switches, OLTs, MikroTik, Windows Server/RDP e eventualmente máquinas de usuários/clientes via assistência remota consentida.

O produto deve substituir ou reduzir dependência de ferramentas como PuTTY/MobaXterm, WinBox, gerenciadores RDP e ferramentas comerciais de assistência remota, mantendo uma base própria, segura, sincronizada e extensível.

## Três bases principais

1. **Multi-SSH/Telnet/MikroTik**
   - Grupos de hosts.
   - Hosts com senha/chave própria ou credencial herdada por grupo.
   - Perfis de fornecedores: MikroTik, Huawei, Cisco, Juniper, ZTE/OLT e outros.
   - Suporte a IPv4 e IPv6, preferindo IPv6 quando disponível.
   - Terminal em abas, múltiplas sessões simultâneas.

2. **RDP/Terminal Server**
   - Lista de servidores Windows/RDP.
   - Porta padrão 3389, customizável.
   - Credenciais por host ou grupo.
   - Múltiplas sessões em abas.
   - Políticas de redirecionamento, NLA e validação de certificado.

3. **Assistência remota NDesk**
   - Usuário local permite acesso a outro operador.
   - Operador pode gerar link temporário para um cliente baixar um agente leve.
   - Conexão com consentimento explícito, tela visível e revogação imediata.
   - Broker/signaling em nuvem própria.
   - IPv6 preferencial e relay quando conexão direta falhar.

## Requisitos transversais

- Sincronização em nuvem entre vários usuários e vários computadores.
- Multiusuário, multiempresa/tenant e RBAC.
- Aprovação gerencial opcional para mudanças sensíveis.
- Segurança forte para credenciais.
- Auditoria de alterações e sessões.
- Código organizado para trabalho paralelo por agentes.
- GitHub privado com CI, branch protection, CODEOWNERS e PRs.
- Windows como plataforma de execução inicial obrigatória.
- Arquitetura extensível para novos protocolos e integrações futuras.

## Premissas adotadas

- “Cloud Code” foi interpretado como **Claude Code**, por causa do uso de subagents, skills e Markdown-driven workflow.
- “Jani/Persisco” foi interpretado como possível transcrição de **Juniper/Cisco**, mas o sistema tratará fornecedores como perfis configuráveis.
- O sistema será interno e autorizado. O módulo de assistência remota não deve oferecer modo oculto, invasivo ou sem consentimento.
- O MVP deve priorizar SSH/Telnet, storage local, sync e RDP antes do NDesk completo, porque assistência remota tem maior complexidade técnica e risco de segurança.

## Fora do escopo inicial

- Quebrar, reverter ou clonar protocolo proprietário do WinBox.
- Acesso remoto não consentido.
- Bypass de MFA, Duo, NLA ou controles do Windows Server.
- Coleta de credenciais de sistemas existentes.
- Suporte macOS/Linux no cliente desktop inicial.
- Marketplace público ou produto comercial externo.

## Nomes temporários

- Nome de trabalho: **RemoteOps Suite**.
- Módulo SSH/Telnet: **MultiTerm**.
- Módulo RDP: **TermServer Desk**.
- Módulo assistência remota: **NDesk**.
- Backend de sync/broker: **RemoteOps Cloud**.
