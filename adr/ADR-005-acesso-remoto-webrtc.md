# ADR-005 — NDesk com WebRTC e consentimento explícito

## Status

Proposta inicial.

## Contexto

Assistência remota estilo TeamViewer/AnyDesk é complexa, sensível e pode ser abusada se mal projetada.

## Decisão

Implementar NDesk como módulo separado com broker próprio, convites temporários, consentimento explícito e transporte WebRTC/relay. Usar Windows.Graphics.Capture para captura. Usar worker Rust apenas se necessário.

## Consequências positivas

- Reuso de tecnologia real-time madura.
- Arquitetura suporta NAT traversal/relay.
- Consentimento e auditoria são centrais.

## Consequências negativas

- Implementação ainda é complexa.
- Codec/input/captura exigem spikes.
- Pode haver alertas de segurança/antivírus se UX/assinatura forem ruins.

## Regras obrigatórias

- Sem acesso oculto.
- Sem persistência silenciosa.
- Sem controle sem consentimento.
- Banner visível e botão encerrar.
