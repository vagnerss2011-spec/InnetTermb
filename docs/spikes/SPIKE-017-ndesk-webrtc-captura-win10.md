# SPIKE-017 — NDesk: stack de transporte WebRTC + captura DXGI para agente .NET (Win10/11)

## Contexto

O `ADR-016-ndesk-pivo-win10-net.md` (em revisão paralela, branch `feature/ndesk-pivot-win10-net`)
formaliza o pivô do NDesk para **Windows 10 (21H2+)/Windows 11 apenas** (Windows 7/8/8.1 saem de
escopo) e para um **agente temporário .NET moderno self-contained**, em vez do binário Win32/C++
nativo de `ADR-007`. Esse ADR deixa explicitamente em aberto, para este spike, a escolha de stack
de transporte: *"a escolha final da stack de transporte nativo (`libwebrtc` completo vs. stack C#
gerenciada) permanece em aberto, a resolver em SPIKE-017/ADR-017"*. Também define DXGI Desktop
Duplication como caminho de captura primário para Win10/11, e pede que captura/input fiquem atrás
de uma interface (`IScreenCaptureProvider`/`IInputInjector`) para viabilizar Linux/macOS no futuro
sem reabrir a arquitetura do agente.

Este spike responde: **qual biblioteca de WebRTC e qual biblioteca de acesso a DXGI usar a partir
de um agente .NET em Win10/11, que sirva de base para portar depois a Linux/macOS?** Também
reconfirma a escolha de TURN self-hosted do `SPIKE-016` com dados atuais.

## Método

Pesquisa com fontes primárias (arquivo LICENSE/LICENCE oficial de cada repositório, releases/tags,
documentação oficial), com **verificação adversarial obrigatória de toda alegação de licença** —
lição do `SPIKE-016` (AGPL do RustDesk). Três agentes de pesquisa dedicados e paralelos (transporte
WebRTC .NET, captura DXGI .NET, reconfirmação TURN), mais verificação adversarial adicional feita
diretamente pelo orquestrador deste spike sobre o achado mais crítico (ver §Transporte). PoC
descartável construído e **executado de fato** nesta máquina Windows 10/11 (não apenas planejado)
para validar viabilidade técnica de P/Invoke contra `libdatachannel` e captura DXGI via
`Vortice.Windows` — ver `tools/spikes/ndesk-webrtc/`.

## Transporte WebRTC

### Candidatos avaliados

**1. SIPSorcery** (`sipsorcery-org/sipsorcery`, C# puro).

Licença — **achado crítico, verificado por fonte primária pelo próprio orquestrador** (não só
pelo agente de pesquisa): o arquivo
[`LICENSE.md`](https://raw.githubusercontent.com/sipsorcery-org/sipsorcery/master/LICENSE.md)
contém **BSD-3-Clause + uma cláusula adicional não padrão de restrição geopolítica** ("BDS" —
Boycott, Divestment, Sanctions): o texto proíbe uso, modificação ou distribuição do software
**dentro de Israel e dos Territórios Ocupados**, e declara que o software "não deve ser usado para
promover as políticas de Apartheid de Israel" em qualquer lugar, até que três condições políticas
específicas (fim da ocupação, igualdade plena para cidadãos árabes-palestinos, direito de retorno
de refugiados palestinos conforme a Resolução 194 da ONU) sejam atendidas. Isso **não é BSD-3-Clause
padrão** — é uma licença com restrição de campo de uso (field-of-use), o que a torna incompatível
com a definição de open source da OSI (que exige não-discriminação contra pessoas, grupos ou campos
de atuação) e introduz **incerteza jurídica não testada em tribunal** sobre um produto comercial.
Verificado também: `SIPSorceryMedia.FFmpeg` (necessário para vídeo H.264/VP9 real) é **LGPL v2.1**,
uma licença separada e adicional.

Manutenção: ativa (commit em 2026-06-30). API cobre ICE/STUN/TURN, DTLS-SRTP, SCTP/data channel
nativamente em C#; vídeo requer pacote companheiro FFmpeg (LGPL) ou um encoder VP8 experimental
em C# puro (lento).

**Desqualificada por licença** — mesma classe de problema que desqualificou o RustDesk no
`SPIKE-016` (lá era AGPL sobre distribuição de cliente fechado; aqui é uma restrição de campo de
uso não testada juridicamente sobre um produto comercial). Não é uma diferença de generosidade da
licença, é uma diferença de **tipo**: nenhuma licença com condição política teria sido aceita pelo
`SPIKE-016` para os componentes que ele avaliou, e o mesmo padrão se aplica aqui.

**2. libdatachannel** (`paullouisageneau/libdatachannel`, C/C++17).

Licença: **MPL-2.0**, confirmada em
[`LICENSE`](https://raw.githubusercontent.com/paullouisageneau/libdatachannel/master/LICENSE) —
sem restrições de campo de uso. Manutenção: ativa (`archived: false` na API do GitHub, commits
recentes). Cobre ICE (RFC8445), STUN/TURN (RFC8489/8656), **DTLS-SRTP** (RFC8261/7350) e SCTP/data
channel (RFC8831) nativamente — inclusive suporte a mídia (RTP/RTX) via a própria biblioteca, ao
contrário do precedente do MeshCentral WebRTC Microstack citado no `SPIKE-016` (que só fazia data
channel). **Sem binding .NET oficial** listado pelos mantenedores (só Rust, Node.js, Unity) — mas a
API C pública (`rtc.h`) é estável, documentada e projetada para consumo via FFI, o que a torna
viável via P/Invoke direto. Confirmado neste spike: **funciona de fato** — ver §PoC.

**3. Microsoft.MixedReality.WebRTC.**

**Morto** — o repositório (`microsoft/MixedRealityWebRTC`) retorna 404 na API do GitHub, sem fork
oficial nem redirecionamento. Descartado sem ressalva.

**4. `libwebrtc` completo (Google/Chromium).**

O `SPIKE-016` já rejeitou embarcar o `libwebrtc` completo para o caminho Win7/legado (~30x maior
que uma alternativa leve, Chromium abandonou Win7/8/8.1). Sem o requisito Win7, o argumento de
compatibilidade desaparece, mas os outros seguem válidos: tamanho de binário, complexidade de
toolchain de build (CMake/GN/Ninja, não um simples `dotnet build`), superfície de API instável
entre versões do Chromium, e **nenhum binding .NET mantido ativamente** encontrado.

### Achado adicional (verificado pelo orquestrador, fora do escopo original da pesquisa)

Existe um pacote NuGet **`DataChannelDotnet`** (MIT, autor "ZetrocDev") que embala um wrapper C#
gerenciado sobre `libdatachannel` 0.24.x, com binários nativos para `win-x64`/`win-x86`/
`linux-x64`/`osx-x64`. **Ressalva de proveniência**: o pacote não lista repositório-fonte no NuGet
(`repository: ""`), é de autor único, e não está marcado como "verified" no NuGet — não foi possível
auditar o código do wrapper gerenciado. Por isso, o PoC deste spike usa esse pacote **apenas como
veículo do binário nativo `datachannel.dll`** (a forma como o NuGet empacota e distribui binários
nativos `runtimes/{rid}/native/` já resolve corretamente a implantação multiplataforma), e os
bindings P/Invoke em `tools/spikes/ndesk-webrtc/Transport/LibDataChannelNative.cs` foram
**escritos e auditados internamente** contra a API C pública e documentada do `libdatachannel`
(`rtc.h`), sem depender do wrapper gerenciado de terceiro. Para produção, isso não é suficiente:
o controle já registrado em `ADR-015` ("build a partir do código-fonte, nunca reassinatura de
binário de terceiro") exige compilar `libdatachannel` a partir do repositório oficial
(`paullouisageneau/libdatachannel`) como parte do pipeline de build do NDesk, não depender de um
binário pré-compilado de proveniência não verificável.

## Captura de tela: Vortice.Windows vs SharpDX

**Vortice.Windows** (`amerkoleci/Vortice.Windows`): licença **MIT**, confirmada no
[`LICENSE`](https://github.com/amerkoleci/Vortice.Windows/blob/master/LICENSE) oficial.
Manutenção ativa (release 3.8.3, suporte a .NET 8/9/10). Implementa `IDXGIOutputDuplication`
completo. Usado por projetos reais (Evergine, Veldrid). **Confirmado neste spike**: compila e
captura corretamente em Windows 11 real (ver §PoC) — 2560x1600 capturado com sucesso via
`IDXGIOutputDuplication`/`AcquireNextFrame`.

**SharpDX** (`sharpdx/SharpDX`): licença MIT também, mas **arquivado desde 29/03/2019**, conforme
aviso oficial no próprio README do repositório ("As of 29 Mar 2019, SharpDX is no longer being
under development or maintenance... This repository is now readonly."). Sem suporte a .NET
moderno, sem correções desde então. Desqualificado por abandono, não por licença.

**Recomendação: `Vortice.Windows`.**

## TURN self-hosted (reconfirmação do SPIKE-016)

O `SPIKE-016` recomendara `coturn` **ou** `eturnal`, citando `eturnal` como "avaliado como mais
consistentemente ativo" na época. Reconfirmado com dados de 2026:

- **`coturn`**: licença BSD-3-Clause
  ([`LICENSE`](https://github.com/coturn/coturn/blob/master/LICENSE)). Releases em cadência
  semanal em 2026 (v4.14.0 em 21/06/2026), incluindo patches de segurança recentes (fim de
  junho/2026). O hiato de 18 meses (2021-2022) citado no `SPIKE-016` **não se repetiu**.
- **`eturnal`**: licença Apache-2.0
  ([`LICENSE`](https://github.com/processone/eturnal/blob/master/LICENSE)). **Estagnado**: última
  release v1.12.2 em 01/05/2025 (~14 meses antes desta pesquisa), commits recentes só de
  Dependabot/CI, sem mudança de núcleo.

**A recomendação se inverte em relação ao SPIKE-016: `coturn` é agora a escolha preferida**, por
cadência de release e patches de segurança ativos. `eturnal` permanece uma opção válida (Apache-2.0
é uma licença mais permissiva para algumas organizações), mas não é mais o candidato "mais ativo".

## PoC — o que foi de fato executado

`tools/spikes/ndesk-webrtc/` (fora de `RemoteOps.sln`, `net10.0-windows`, `win-x64`). Pipeline
real, **executado nesta máquina Windows 11** (não simulado):

1. Captura 1 monitor via `Vortice.Direct3D11`/`Vortice.DXGI` (`IDXGIOutputDuplication`).
2. Codec **placeholder** (diff de frame anterior + GZip — deliberadamente **não** H.264/VP8; ver
   §Pendências e `Codec/PlaceholderFrameCodec.cs`).
3. Transporte via `libdatachannel` P/Invoke: 2 `PeerConnection`s no mesmo processo ("agente" e
   "viewer"), SDP/candidatos ICE trocados diretamente em memória (sem broker — "loopback"),
   DataChannel SCTP real, fragmentação manual em chunks (a negociação DCEP travou o
   max-message-size efetivo em 256KiB nesta máquina mesmo pedindo mais via
   `rtcConfiguration.maxMessageSize` — não investigado a fundo, fora de escopo; qualquer stack de
   produção precisaria fragmentar frames grandes de qualquer forma).
4. Latência (capture→entrega) e FPS medidos de verdade.

### Resultado medido (2026-07-01, Windows 11, monitor 2560x1600)

| Métrica | Valor |
|---|---|
| Frames entregues | 47 em 8,03s |
| FPS médio (entregue) | 5,85 |
| Latência média | 39,86 ms |
| Latência p50 | 39,78 ms |
| Latência p95 | 56,56 ms |
| Payload médio | 208.511 bytes |
| Bitrate médio | 9.760,7 kbps |

**Interpretação**: a latência (~40ms p50) é capture→entrega via DataChannel **local** (mesmo
processo, sem STUN/TURN real, sem rede) — mede o overhead do pipeline `libdatachannel`/SCTP em si,
não RTT de rede. FPS baixo e bitrate alto são esperados do codec placeholder (sem compressão de
vídeo real) contra uma tela com conteúdo mudando, não uma limitação do transporte. O resultado
prova viabilidade técnica de P/Invoke + DXGI, não desempenho final do produto.

## Pendências (não resolvidas por este spike, escopo de spikes futuros já mapeados)

- **Codec de vídeo real (H.264/OpenH264 ou VP8)**: item próprio, já listado em
  `docs/22-ndesk-performance-legacy-windows.md` ("Spikes obrigatórios" #3) e `docs/15` (SPIKE-010,
  item "Codec") — fora de escopo deste spike de transporte+captura.
- **NAT/CGNAT/TURN real**: este PoC é loopback local (127.0.0.1); teste de NAT real é o SPIKE-012
  já mapeado em `docs/15`.
- **Motivo exato do teto de 256KiB no max-message-size negociado do `libdatachannel`** apesar de
  `rtcConfiguration.maxMessageSize` maior — não investigado; mitigado via fragmentação manual no
  PoC, que é a abordagem correta de qualquer forma para frames de vídeo grandes.
- **Build de `libdatachannel` a partir do código-fonte oficial** para produção, em vez do binário
  de terceiro usado neste PoC — controle já exigido por `ADR-015`.
- **Abstração `IScreenCaptureProvider`/`IInputInjector`** (prevista em `ADR-016` para portabilidade
  futura) não foi desenhada neste spike — este PoC captura direto, sem a interface; fica para a
  implementação real do agente.

## Riscos

1. **Licença do SIPSorcery** — se não descartado corretamente, o risco jurídico é real e da mesma
   classe que quase levou o projeto a adotar RustDesk (AGPL) no `SPIKE-016`: uma licença com
   condição não padrão sobre um produto comercial.
2. **Proveniência do binário `datachannel.dll`** usado no PoC (via `DataChannelDotnet`, autor
   único, sem repositório-fonte vinculado) — aceitável para um PoC descartável, **não** aceitável
   para produção sem builda-lo a partir da fonte oficial.
3. **P/Invoke contra uma API C sem binding .NET oficial** é mais trabalho de manutenção que usar
   uma biblioteca C# nativa — mitigado pelo fato de a API pública (`rtc.h`) ser estável e pequena
   no subconjunto necessário (confirmado: ~25 funções cobrem PeerConnection + DataChannel).
4. **Teto de mensagem SCTP não explicado** (256KiB observado vs. configurado maior) é um risco
   técnico residual a investigar antes de comprometer o design de fragmentação de produção.

## Recomendação

**Transporte: `libdatachannel` (MPL-2.0) via P/Invoke direto contra a API C pública, buildado a
partir do código-fonte oficial em produção.** SIPSorcery é desqualificado por licença (cláusula
BDS não padrão, incompatível com um produto comercial de distribuição ampla); Microsoft.MixedReality.WebRTC
está morto; `libwebrtc` completo é desproporcional para Win10/11 mesmo sem a restrição de Windows 7.

**Captura: `Vortice.Windows` (MIT, ativo)**, substituindo `SharpDX` (arquivado desde 2019).

**TURN: `coturn` (BSD-3)**, atualizando a recomendação do `SPIKE-016` — agora com cadência de
release mais consistente que `eturnal` em 2026.

Ver `adr/ADR-017-ndesk-stack-transporte-midia.md` para a decisão formal.

## Próximos passos

1. Atualizar `docs/15-pesquisa-e-spikes.md` com esta entrada (feito neste PR).
2. Escrever `adr/ADR-017-ndesk-stack-transporte-midia.md` (feito neste PR, Status "Proposta").
3. Spike dedicado de codec de vídeo real (H.264/OpenH264 vs VP8) antes de qualquer implementação
   de produção — já mapeado como pendência de `docs/22`/`docs/15` SPIKE-010.
4. Investigar o teto de 256KiB de max-message-size do `libdatachannel` antes de fixar o design de
   fragmentação de frames em produção.
5. Desenhar a interface `IScreenCaptureProvider`/`IInputInjector` prevista em `ADR-016` quando a
   implementação real do agente começar.
6. `security-agent` deve revisar o pipeline de build de `libdatachannel` a partir da fonte antes de
   qualquer binário chegar a produção, conforme o controle já registrado em `ADR-015`.
