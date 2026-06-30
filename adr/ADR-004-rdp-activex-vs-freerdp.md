# ADR-004 — RDP: ActiveX primeiro, FreeRDP em spike

## Status

Proposta inicial.

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
