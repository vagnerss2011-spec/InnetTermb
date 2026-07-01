# ADR-007 — NDesk Temporary Agent Win32 nativo para Windows legado

## Status

Aceita para spike e MVP do agente temporário. Confirmada pelo SPIKE-016 (`docs/spikes/SPIKE-016-ndesk-buy-vs-build.md` / `adr/ADR-015-ndesk-buy-vs-build.md`), que descobriu um risco novo de toolchain — ver "Critério de revisão futura" abaixo.

## Contexto

O acesso remoto é usado de forma esporádica em muitas máquinas diferentes, algumas antigas, e precisa evitar bloqueios comerciais de ferramentas de terceiros. O agente baixado por link deve abrir rápido e funcionar sem exigir Java, WebView2 ou .NET moderno. Há requisito operacional para Windows 10 e Windows 7.

## Decisão

O NDesk terá um agente temporário separado do Desktop principal, implementado como binário Win32/C++ nativo, com runtime estático quando possível, UI de consentimento nativa, captura por APIs Windows compatíveis e transporte via WebRTC/relay ou fallback TCP/TLS conforme plataforma.

## Consequências positivas

- Reduz pré-requisitos no computador atendido.
- Melhora chance de funcionamento em Windows 7/10 antigo.
- Mantém o Desktop principal em C#/.NET/WPF sem sacrificar compatibilidade do agente.
- Permite otimização de captura, codec e input.

## Consequências negativas

- Aumenta complexidade de build e testes.
- Exige assinatura e cuidado com antivírus/EDR.
- Windows 7 terá captura e performance mais limitadas.
- WebRTC nativo e codec exigem spikes técnicos.

## Controles

- Sem modo oculto.
- Sem persistência silenciosa.
- Link temporário e token de uso único.
- Consentimento visível.
- Banner durante sessão.
- Modo administrador separado e consentido.
- Logs sem conteúdo de tela e sem segredos.
- Testes de Windows 7/10/11 em laboratório.

## Critério de revisão futura

Revisar esta ADR se:

- o suporte a Windows 7 for abandonado;
- uma biblioteca WebRTC nativa não atender requisitos;
- o agente C++ gerar custo de manutenção excessivo;
- um runtime alternativo single-file provar ser mais seguro e compatível;
- **(SPIKE-016)** o Visual Studio 2022 chegar ao fim do suporte mainstream (~jan/2027) sem um caminho de sucessor viável para compilar/assinar o agente visando Windows 7 — o Visual Studio 2026 atual já removeu Windows 7 como plataforma de deployment suportada, sem workaround documentado equivalente ao antigo toolset `v141_xp` do Windows XP;
- **(SPIKE-016)** o spike técnico dedicado de `libdatachannel` em Windows 7 SP1 (recomendado em `docs/spikes/SPIKE-016-ndesk-buy-vs-build.md`) mostrar inviabilidade sem alternativa leve equivalente.
