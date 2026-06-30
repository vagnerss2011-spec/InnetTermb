# ADR-004 — RDP: ActiveX primeiro, FreeRDP em spike

## Status

Aceita. Implementada em `feature/integration-rdp` (ver ADR-014 para detalhes de
hospedagem, políticas, feature flag e o pivot de empacotamento COM).

## Contexto

O produto precisa abrir RDP/Terminal Server com boa compatibilidade Windows.

## Decisão

Usar Microsoft Remote Desktop ActiveX/MSTSCAX no MVP, hospedado via WPF/WinForms interop. Executar spike FreeRDP como alternativa.

## Consequências positivas

- Melhor compatibilidade inicial com Windows Server.
- Menos implementação de protocolo.
- Aproveita stack Microsoft.

## Consequências negativas

- ActiveX/COM é legado.
- Pode dificultar UI moderna.
- Pode exigir workarounds de resize/foco.

## Critério para trocar por FreeRDP

Trocar ou complementar se ActiveX falhar em abas, foco, eventos, distribuição ou políticas obrigatórias.

## Implementação MVP

- Controle hospedado via `WindowsFormsHost` em `RdpTabView` (WPF) — análogo ao
  WebView2 do terminal (`TerminalTabView`), mas com `AxMsRdpClient9NotSafeForScripting`
  (MSTSCAX) no lugar de um host de browser.
- Lógica de configuração de conexão (host/porta/usuário/políticas de redirecionamento)
  isolada em `RdpConnectionConfigBuilder`/`RdpSessionProvider` — pura, sem COM,
  testável em CI; só a camada de View (`RdpTabView.xaml.cs`) toca o controle ActiveX.
- **Spike FreeRDP não foi executado nesta frente.** O critério de troca definido
  acima neste ADR permanece válido como gatilho para reavaliar — esta PR não o
  invalida nem o resolve, apenas implementa o caminho ActiveX já decidido.
- Atrás da feature flag `rdp.enabled` (default OFF, lida de
  `REMOTEOPS_FEATURE_FLAGS` via `EnvironmentFeatureFlags`) até validação manual
  em laboratório (ver docs/08 §Status de implementação e ADR-014 §Pendências).
  A flag gateia tanto a visibilidade do botão "Conectar RDP" no Inspector quanto
  o roteamento em `MainViewModel.OnSessionRequested` — defesa em profundidade,
  não apenas ocultação de UI.
- **Desvio relevante em relação ao plano original:** o mecanismo de interop COM
  efetivamente usado (binários `TlbImp`/`AxImp` checados em `src/RemoteOps.Rdp/lib/`)
  não é o `<COMReference>` MSBuild originalmente previsto — ver ADR-014 Decisão 6
  para o motivo (incompatibilidade com `dotnet build`) e os comandos de
  regeneração.
