# 15 — Pesquisa e spikes técnicos

## Objetivo

Validar riscos antes de comprometer muitos agentes em implementação longa. Spikes devem ser curtos, com resultado mensurável, código PoC quando útil e recomendação clara.

## Spikes prioritários

### SPIKE-001 — WPF + WebView2 + xterm.js

Pergunta: o terminal fica responsivo em múltiplas abas?

Entregas:

- PoC com xterm.js.
- Bridge C#↔JS.
- Resize.
- Copy/paste.
- Medição com 10 abas.

### SPIKE-002 — SSH.NET compatibilidade

Pergunta: SSH.NET atende equipamentos reais da empresa?

Entregas:

- Testar Linux OpenSSH.
- Testar MikroTik.
- Testar Cisco/Huawei/Juniper/ZTE disponíveis.
- Mapear algoritmos incompatíveis.

### SPIKE-003 — Telnet adapter

Pergunta: implementação mínima atende equipamentos legados?

Entregas:

- TCP Telnet negotiation mínima.
- Teste com equipamento/simulador.
- Definir limitações.

### SPIKE-004 — RDP ActiveX em WPF

Pergunta: ActiveX funciona bem em abas WPF?

Entregas:

- PoC MSTSCAX em WindowsFormsHost.
- Eventos de connect/disconnect.
- Resize.
- NLA.
- Clipboard/drive policy.

### SPIKE-005 — FreeRDP comparação

Pergunta: FreeRDP deve substituir ou complementar MSTSCAX?

Entregas:

- Compilar/rodar no Windows.
- Avaliar embedding.
- Avaliar NLA/cert/clipboard.
- Decisão ADR.

### SPIKE-006 — MikroTik WinBox Runner

Pergunta: chamar o WinBox oficial externo atende o fluxo operacional do MVP?

Entregas:

- Validar execução de `winbox.exe` com host, usuário e senha vazia.
- Validar senha com caracteres especiais sem quebrar aspas.
- Validar IPv6 com colchetes e porta customizada.
- Validar workspace/sessão.
- Validar RoMON se necessário.
- Testar hash/manifesto do executável.
- Confirmar que logs não expõem senha.
- Definir política padrão de senha via argumento.

### SPIKE-007 — RouterOS API-SSL/REST

Pergunta: API cobre telas MikroTik futuras?

Entregas:

- Conectar em RouterOS API-SSL.
- Listar interfaces, identity, services.
- Testar REST em v7.
- Mapear permissões necessárias.

### SPIKE-008 — SQLCipher + DPAPI

Pergunta: storage local protege segredos e mantém performance?

Entregas:

- DB criptografado.
- Chave protegida por DPAPI.
- Teste de restart.
- Teste de laptop/usuário diferente não abrir.

### SPIKE-009 — Sync offline-first

Pergunta: outbox/changelog resolve fluxo multiusuário?

Entregas:

- Dois clientes simulados.
- Conflito de host.
- Conflito de segredo.
- SignalR hint.

### SPIKE-010 — NDesk Win10 captura/WebRTC

Pergunta: captura e controle remoto são viáveis com baixa latência em Windows moderno?

Entregas:

- Captura Windows.Graphics.Capture.
- Alternativa DXGI.
- Stream WebRTC local.
- DataChannel para input autorizado.
- Teste em duas redes.
- Medição de latência.

### SPIKE-011 — NDesk Windows 7 legado

Pergunta: agente temporário Win32 consegue rodar em Windows 7 SP1 sem Java/WebView2/.NET moderno?

Entregas:

- Binário Win32 assinado em modo teste.
- Captura GDI BitBlt.
- Detecção de regiões alteradas.
- Compressão básica.
- Relay TCP/TLS 443.
- UI de consentimento nativa.
- Teste em Windows 7 SP1 limpo.
- Medição de CPU, memória e FPS.

### SPIKE-012 — NDesk conexão lenta/NAT

Pergunta: o fluxo fica utilizável atrás de NAT/CGNAT e com link ruim?

Entregas:

- STUN/TURN/relay próprio.
- Fallback TCP/TLS 443.
- Testes com 1, 2 e 5 Mbps.
- Testes com 80 ms, 150 ms e 250 ms RTT.
- Testes com 1%, 3% e 5% de perda.
- Perfil de baixa banda.
- Métricas de sessão.

### SPIKE-013 — NDesk UAC/admin consentido

Pergunta: como permitir suporte administrativo sem burlar UAC?

Entregas:

- Fluxo de solicitação de modo admin.
- Helper temporário visível ou execução elevada.
- Remoção ao final.
- Limitações documentadas.
- Revisão de segurança.

### SPIKE-014 — Instalador e assinatura

Pergunta: melhor formato de distribuição interna?

Entregas:

- MSIX/MSI/Velopack comparados.
- Assinatura com SignTool.
- Atualização controlada.
- Resultado em ADR.

### SPIKE-015 — Changelog/release automation

Pergunta: como garantir versionamento limpo com muitos agentes?

Entregas:

- Checagem de changelog em PR.
- Script de versão.
- Geração de release notes.
- Validação de tags por componente.

### SPIKE-016 — NDesk: comprar solução self-hosted vs construir do zero

Pergunta: para o módulo NDesk, é melhor adaptar uma solução self-hosted open-source pronta (RustDesk, MeshCentral, Apache Guacamole) ou confirmar o caminho de construir (`ADR-005`/`ADR-007`)?

Entregas:

- Pesquisa com fontes primárias (LICENSE oficial, documentação de self-hosting, NVD/GitHub Security Advisories) para cada candidato, com verificação adversarial de licença e CVEs.
- Matriz de decisão candidato × critério (licença, consentimento/revogação/auditoria/sem modo oculto, Windows 7 SP1, NAT/CGNAT, segurança/CVEs, esforço de adaptação, custo operacional).
- Parecer independente do `security-agent` (ótica de risco) e do `ndesk-agent` (ótica de fit arquitetural).
- `docs/spikes/SPIKE-016-ndesk-buy-vs-build.md` (relatório completo) e `adr/ADR-015-ndesk-buy-vs-build.md` (decisão).

**Resultado:** confirmado o caminho "construir" (`ADR-005`/`ADR-007`). RustDesk é desqualificado por licença (AGPL-3.0 incompatível com distribuir agente fechado a terceiros) e por permitir oficialmente compor um cliente sem indicador visível/sem botão de parar. MeshCentral permite consentimento configurável para silencioso e tem AMT out-of-band sem consentimento possível; instalação persistente por padrão conflita com "sem serviço no MVP". Apache Guacamole não resolve o problema (gateway para infraestrutura já gerenciada, sem conceito de agente/consentimento ad-hoc). `security-agent` e `ndesk-agent`, de forma independente, convergiram em não recomendar nenhum candidato de compra. Risco novo descoberto: Visual Studio 2026 removeu Windows 7 como plataforma de deployment; time depende do VS2022 (mainstream até ~jan/2027) — critério de revisão futura incorporado à `ADR-007`. Recomendado `libdatachannel`+`coturn`/`eturnal` em vez de embarcar o `libwebrtc` completo do Chromium (que já abandonou Windows 7/8/8.1).

## Agentes de pesquisa sugeridos

- `research-agent`: coordena spikes e registra evidências.
- `desktop-shell-agent`: SPIKE-001 e 004.
- `ssh-telnet-agent`: SPIKE-002 e 003.
- `mikrotik-agent`: SPIKE-006 e 007.
- `security-agent`: SPIKE-008 e 013.
- `cloud-sync-agent`: SPIKE-009.
- `ndesk-agent`: SPIKE-010, 011 e 012.
- `devops-agent`: SPIKE-014 e 015.
- `release-manager-agent`: SPIKE-015.

## Formato de entrega de spike

Cada spike deve gerar:

- Contexto.
- Hipótese.
- Código PoC, se houver.
- Como reproduzir.
- Resultado.
- Métricas.
- Riscos.
- Recomendação.
- ADR se alterar decisão.
