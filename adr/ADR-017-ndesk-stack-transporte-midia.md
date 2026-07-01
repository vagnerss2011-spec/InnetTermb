# ADR-017 — NDesk: stack de transporte WebRTC e captura para o agente .NET (Win10/11)

## Status

Proposta (spike concluído, aguardando revisão do orquestrador). Complementa `ADR-005`
(WebRTC/consentimento) e resolve a escolha de stack de transporte deixada em aberto por
`ADR-016` (pivô do agente para .NET, Win10/11 apenas). Reconfirma parcialmente `ADR-015`/
`SPIKE-016` no critério de TURN self-hosted.

## Contexto

`ADR-016-ndesk-pivo-win10-net.md` (revisão paralela, branch `feature/ndesk-pivot-win10-net`)
formalizou o pivô do agente NDesk de Win32/C++ nativo (`ADR-007`) para **.NET moderno
self-contained**, alvo **Windows 10 21H2+/Windows 11 apenas**, com DXGI Desktop Duplication como
captura primária, e deixou explicitamente em aberto: *"a escolha final da stack de transporte
nativo (`libwebrtc` completo vs. stack C# gerenciada) permanece em aberto, a resolver em
SPIKE-017/ADR-017"*. `ADR-005` já recomendava, com base no `SPIKE-016`, evitar embarcar o
`libwebrtc` completo do Chromium e preferir `libdatachannel` + `coturn`/`eturnal` — mas essa
recomendação foi feita no contexto do agente Win32/C++ nativo, antes do pivô para .NET. Este ADR
resolve a escolha concreta de bibliotecas **.NET** para transporte e captura, com base em
`docs/spikes/SPIKE-017-ndesk-webrtc-captura-win10.md` (pesquisa com fontes primárias, verificação
adversarial de licença, e PoC executado de fato nesta máquina Windows 11).

## Decisão

### 1. Transporte WebRTC: `libdatachannel` via P/Invoke direto

O agente .NET consumirá **`libdatachannel`** (`paullouisageneau/libdatachannel`, MPL-2.0, C/C++17)
através de **bindings P/Invoke escritos e mantidos internamente** contra a API C pública e
documentada (`include/rtc/rtc.h`), não através de nenhum wrapper gerenciado de terceiro.

Rejeitado **SIPSorcery** (C# puro, que seria a opção de menor esforço de integração): o arquivo
`LICENSE.md` do repositório oficial contém, além do BSD-3-Clause, uma **cláusula adicional não
padrão de restrição geopolítica** ("BDS") que proíbe uso/distribuição em Israel e nos Territórios
Ocupados e impõe uma condição política global de "não promover Apartheid" — verificado por fonte
primária (`https://raw.githubusercontent.com/sipsorcery-org/sipsorcery/master/LICENSE.md`), não
por alegação de terceiro. Isso é uma restrição de campo de uso, incompatível com a definição de
open source da OSI, e introduz risco jurídico não testado sobre um produto comercial — mesma
classe de bloqueio que a licença AGPL do RustDesk no `SPIKE-016` (ADR-015), ainda que por um motivo
diferente (lá era copyleft de rede sobre distribuição de cliente derivado; aqui é uma condição de
campo de uso).

Rejeitado **Microsoft.MixedReality.WebRTC**: repositório removido/arquivado (404 confirmado via
API do GitHub), sem sucessor oficial.

Rejeitado **`libwebrtc` completo** (Google/Chromium): mesmo sem o requisito de Windows 7 (que já
o desqualificava por si só no `SPIKE-016`), o tamanho de binário, a complexidade de toolchain de
build (CMake/GN/Ninja) e a ausência de um binding .NET mantido ativamente continuam
desproporcionais para o caso de uso.

### 2. Captura de tela: `Vortice.Windows`

**`Vortice.Windows`** (MIT, ativo — release 3.8.3, suporte .NET 8/9/10) é a biblioteca de acesso
a DXGI Desktop Duplication (`IDXGIOutputDuplication`) a partir de .NET, confirmando o caminho de
captura primário Win10/11 já definido por `ADR-016`. **`SharpDX`** é rejeitado por abandono —
arquivado pelos próprios mantenedores desde 29/03/2019, sem suporte a .NET moderno.

### 3. TURN self-hosted: `coturn` (atualiza SPIKE-016)

O `SPIKE-016`/`ADR-015` recomendara `coturn` **ou** `eturnal` de forma equivalente. Reconfirmado
com dados de 2026 (`SPIKE-017`): `coturn` (BSD-3-Clause) está em cadência de release semanal com
patches de segurança ativos; `eturnal` (Apache-2.0) está estagnado desde maio/2025. **`coturn` é
agora a escolha preferida**, sem alterar a conclusão de que ambos são candidatos aceitáveis
(`eturnal` permanece uma opção válida se a licença Apache-2.0 for preferida por algum motivo
organizacional).

### 4. Fora de escopo desta decisão

- **Codec de vídeo** (H.264/OpenH264 vs VP8): item próprio e separado, já mapeado em `docs/22`
  ("Spikes obrigatórios" #3) e `docs/15` (SPIKE-010, item "Codec"). Este ADR decide transporte e
  captura, não codec.
- **NAT/CGNAT/TURN real**: o PoC deste spike é loopback local; validação com relay/TURN real é o
  `SPIKE-012` já mapeado em `docs/15`.
- **Abstração `IScreenCaptureProvider`/`IInputInjector`** prevista em `ADR-016` para portabilidade
  Linux/macOS futura: não desenhada por este ADR, fica para a implementação real do agente —
  `libdatachannel` já tem binários para `linux-x64`/`osx-x64` disponíveis (confirmado via
  distribuição do NuGet usado no PoC), o que é compatível com esse roadmap sem comprometer nada
  agora.

## Consequências positivas

- Transporte com licença permissiva sem restrição de campo de uso (MPL-2.0), evitando repetir o
  quase-erro do `SPIKE-016` com o AGPL do RustDesk, desta vez numa biblioteca que seria a opção
  "mais fácil" de integrar (C# puro).
- Captura via biblioteca ativa e mantida (`Vortice.Windows`), eliminando o risco de dependência de
  `SharpDX` arquivado desde 2019.
- `libdatachannel` cobre mídia (RTP/SRTP) nativamente, não só data channel — evita o precedente
  limitado do MeshCentral WebRTC Microstack citado no `SPIKE-016`.
- `coturn` com cadência de release ativa reduz o risco de "bus factor" que preocupava o `SPIKE-016`.
- PoC real (não só pesquisa de papel) confirma viabilidade técnica antes de comprometer a
  implementação de produção — captura DXGI e transporte P/Invoke funcionam de fato em Windows 11.
- `libdatachannel` já distribui binários para Linux/macOS, compatível com o roadmap de
  portabilidade futura sem custo adicional agora.

## Consequências negativas

- **Sem binding .NET oficial para `libdatachannel`**: os bindings P/Invoke em
  `tools/spikes/ndesk-webrtc/Transport/LibDataChannelNative.cs` (subconjunto do PoC) precisam virar
  um binding de produção mantido internamente, com testes próprios — mais esforço de manutenção
  contínua que consumir uma biblioteca C# gerenciada nativamente.
- **Binário nativo de terceiro**: o PoC usou um pacote NuGet (`DataChannelDotnet`) de proveniência
  não totalmente verificável (autor único, sem repositório-fonte vinculado) apenas como veículo do
  binário `datachannel.dll`. **Controle obrigatório antes de produção**: buildar `libdatachannel` a
  partir do código-fonte oficial (`paullouisageneau/libdatachannel`) como parte do pipeline de
  build/release do NDesk, conforme já exigido por `ADR-015` ("build a partir do código-fonte, nunca
  reassinatura de binário de terceiro").
- **Teto de max-message-size do `libdatachannel` observado em 256KiB** no PoC, mesmo configurando
  um valor maior via `rtcConfiguration.maxMessageSize` — causa raiz não investigada; mitigado com
  fragmentação manual de frames grandes, que seria necessária de qualquer forma para vídeo.
- Reabrir a comparação de transporte também significa que qualquer trabalho já feito assumindo
  `SIPSorcery` (nenhum identificado neste momento) precisaria ser descartado — registrado aqui para
  rastreabilidade, não porque exista tal trabalho hoje.

## Alternativas consideradas

Ver `docs/spikes/SPIKE-017-ndesk-webrtc-captura-win10.md` para a matriz completa. Resumo: SIPSorcery
(rejeitado por licença), Microsoft.MixedReality.WebRTC (rejeitado por abandono), `libwebrtc`
completo (rejeitado por desproporcionalidade), SharpDX (rejeitado por abandono), `eturnal`
(aceitável, mas não mais preferido sobre `coturn`).

## Critério de revisão futura

Revisar este ADR se:

- a cláusula de licença do SIPSorcery for removida/relicenciada pelos mantenedores de forma que
  volte a ser BSD-3-Clause padrão, e a economia de esforço de integração (C# puro vs. P/Invoke)
  justificar reabrir a comparação;
- surgir um binding .NET oficialmente mantido para `libdatachannel` que reduza o custo de
  manutenção dos bindings P/Invoke internos;
- o teto de max-message-size de 256KiB se provar uma limitação estrutural (não só de configuração)
  que inviabilize o design de fragmentação de frames de vídeo em produção;
- o roadmap de Linux/macOS citado em `ADR-016` for formalmente priorizado — neste caso, revisar se
  `libdatachannel` nessas plataformas atende os mesmos critérios validados aqui para Windows;
- `coturn` sofrer novo hiato de manutenção prolongado (repetição do padrão 2021-2022 já citado no
  `SPIKE-016`) — neste caso, reavaliar `eturnal` como preferência.

## Referências

- `docs/spikes/SPIKE-017-ndesk-webrtc-captura-win10.md` — relatório completo, matriz de decisão,
  fontes primárias citadas, PoC executado e resultado medido.
- `adr/ADR-016-ndesk-pivo-win10-net.md` — pivô para .NET/Win10/11 que disparou este spike.
- `adr/ADR-005-acesso-remoto-webrtc.md` — WebRTC/consentimento, complementado por este ADR.
- `adr/ADR-015-ndesk-buy-vs-build.md` / `docs/spikes/SPIKE-016-ndesk-buy-vs-build.md` — precedente
  de verificação adversarial de licença (AGPL do RustDesk) e controle de build-from-source.
- `docs/22-ndesk-performance-legacy-windows.md`, `docs/15-pesquisa-e-spikes.md` — codec de vídeo e
  demais spikes do NDesk permanecem itens separados, não resolvidos por este ADR.
- `tools/spikes/ndesk-webrtc/` — PoC descartável, fora de `RemoteOps.sln`.
