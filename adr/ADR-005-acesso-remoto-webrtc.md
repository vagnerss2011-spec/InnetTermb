# ADR-005 — NDesk com WebRTC e consentimento explícito

## Status

Confirmada pelo SPIKE-016 (`docs/spikes/SPIKE-016-ndesk-buy-vs-build.md` / `adr/ADR-015-ndesk-buy-vs-build.md`), com revisões — ver seção "Atualização" abaixo.

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

## Atualização (SPIKE-016 / ADR-015)

O SPIKE-016 avaliou comprar/adaptar uma solução self-hosted (RustDesk, MeshCentral, Apache Guacamole) em vez de construir, e **confirmou** esta ADR. Dois pontos ficam mais específicos a partir da evidência do spike:

- **Reuso de tecnologia real-time madura, mas não o `libwebrtc` completo do Chromium**: embarcar o `libwebrtc` completo não é viável para Windows 7 (Chromium abandonou Win7/8/8.1 desde o Chrome 109/jan-2023) e resulta em binário ~30x maior que uma alternativa leve. Preferir **`libdatachannel`** (MPL-2.0, C++17) para o data channel/mídia nativa, e **`coturn`** (BSD-3) ou **`eturnal`** (Apache-2.0) para TURN self-hosted. Suporte do `libdatachannel` a Windows 7 SP1 ainda não está confirmado nem negado em fonte oficial — requer spike técnico dedicado antes de comprometer a arquitetura (ver SPIKE-011 e o novo spike recomendado em `docs/spikes/SPIKE-016-ndesk-buy-vs-build.md`).
- Detalhes completos, matriz de decisão e fontes primárias: `docs/spikes/SPIKE-016-ndesk-buy-vs-build.md`.
