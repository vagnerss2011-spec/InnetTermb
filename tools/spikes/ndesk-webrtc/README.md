# SPIKE-017 — PoC descartável: DXGI + libdatachannel (Win10/11, .NET)

**Descartável.** Fora de `RemoteOps.sln` de propósito. Não referenciar a partir de nenhum
projeto de produto. Relatório completo: `docs/spikes/SPIKE-017-ndesk-webrtc-captura-win10.md`.
Decisão: `adr/ADR-017-ndesk-stack-transporte-midia.md`.

## O que mede

1. Captura 1 monitor via **DXGI Desktop Duplication** (`Vortice.Windows`, MIT).
2. "Encoda" cada frame com um **codec placeholder** (diff de frame anterior + GZip — **não**
   é H.264/VP8; ver `Codec/PlaceholderFrameCodec.cs` para o porquê).
3. Envia via **DataChannel libdatachannel** (MPL-2.0) entre 2 `PeerConnection`s no mesmo
   processo ("agente" e "viewer"), com SDP/candidatos ICE trocados diretamente em memória
   (sem broker/signaling — é isso que "loopback" significa aqui).
4. Imprime FPS entregue e latência (capture→entrega) média/p50/p95, e bitrate médio.

## Como rodar

Requer Windows 10/11 com GPU real (WDDM), sessão de console local (não RDP), .NET 10 SDK.

```
cd tools/spikes/ndesk-webrtc
dotnet run -- <segundosDeDuracao=10> <indiceDoMonitor=0>
```

Se a captura DXGI ou o DataChannel falharem neste ambiente, o programa imprime o motivo
provável e sai com código 1 em vez de travar — ver `Program.cs`.

## O que este PoC PROVA e o que NÃO prova

Prova:
- `Vortice.Windows` acessa `IDXGIOutputDuplication` de .NET moderno sem SharpDX.
- `libdatachannel` (biblioteca C, sem binding .NET oficial) é consumível via P/Invoke direto
  contra a API pública `rtc.h`, incluindo a negociação real ICE/DTLS/SCTP e DataChannel —
  não é só teoria de licença, o binário funciona a partir de C#.
- O pipeline capture→encode→transporte→entrega end-to-end é medível.

NÃO prova (pendências, ver SPIKE-017 §Pendências):
- NAT/CGNAT/TURN real (isto é loopback 127.0.0.1, sem rede) — SPIKE-012.
- Codec de vídeo real (H.264/VP8) — o placeholder aqui é só para ter algo mensurável;
  escolha de codec é item próprio de `docs/22` (Spikes obrigatórios #3) e `docs/15` (SPIKE-010).
- Suporte Windows 7 — fora de escopo do NDesk desde ADR-016.
- Que o pacote NuGet `DataChannelDotnet` (usado aqui só como veículo do binário nativo
  `datachannel.dll`) seja adequado para produção — ver ressalva de proveniência na SPIKE-017;
  produção deve buildar `libdatachannel` a partir do código-fonte oficial.

## Resultado de referência (medido nesta máquina em 2026-07-01)

```
Captura DXGI inicializada: 2560x1600
Frames capturados/enviados: 47
FPS médio (entregue):    5,85
Latência média (ms):     39,86
Latência p50 (ms):       39,78
Latência p95 (ms):       56,56
Payload médio (bytes):   208511
Bitrate médio (kbps):    9760,7
```

FPS baixo e bitrate alto são esperados do codec placeholder (diff+GZip sem codec de vídeo
real) contra uma tela com conteúdo mudando — não são uma medição de qualidade do transporte
em si. Ver SPIKE-017 para interpretação.
