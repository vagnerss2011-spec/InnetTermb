# ADR-005 — NDesk com WebRTC e consentimento explícito

## Status

Confirmada pelo SPIKE-016 (`docs/spikes/SPIKE-016-ndesk-buy-vs-build.md` / `adr/ADR-015-ndesk-buy-vs-build.md`), com revisões — ver seção "Atualização" abaixo. **Revisada novamente pela `ADR-016` (`adr/ADR-016-ndesk-pivo-win10-net.md`)**: a restrição de Windows 7 é removida e a escolha de stack de transporte volta a ficar em aberto — ver "Atualização (ADR-016)" abaixo.

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

## Atualização (ADR-016)

A `ADR-016` remove Windows 7/8/8.1 do escopo do NDesk e pivota o agente temporário de Win32/C++ para .NET moderno self-contained (ver `adr/ADR-016-ndesk-pivo-win10-net.md`). Isso muda a motivação por trás da recomendação de `libdatachannel` acima: o argumento decisivo contra embarcar o `libwebrtc` completo era, especificamente, sua inviabilidade em Windows 7 — argumento que **deixa de se aplicar** com o alvo restrito a Windows 10/11.

A restrição de Windows 7 é removida desta ADR. A escolha de stack de transporte volta a ficar **em aberto entre**:

- **`libwebrtc`/stack C++ nativa** (ex.: `libdatachannel` + `coturn`/`eturnal`), acessada via interop a partir do agente .NET; ou
- **Stack C# gerenciada** para WebRTC (ex.: bindings/wrappers gerenciados existentes no ecossistema .NET), evitando interop nativo no agente e mantendo consistência de linguagem com o restante do módulo.

A decisão final entre essas duas opções fica para um spike técnico dedicado (**SPIKE-017**) e a correspondente **ADR-017**, que devem avaliar maturidade, tamanho de binário, esforço de interop e compatibilidade com o agente .NET self-contained definido em `ADR-016`. Até lá, ambas permanecem candidatas válidas.
