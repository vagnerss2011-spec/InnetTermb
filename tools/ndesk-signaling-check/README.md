# ndesk-signaling-check

Verificador de integração do **signaling** do NDesk Broker. Ferramenta de dev/CI (fora da
`RemoteOps.sln`), cross-platform. Opera os **dois lados** — operador autenticado + agente
anônimo — num único processo contra um broker real e valida o hub SignalR de ponta a ponta.

## O que prova

| Check | Garante |
|-------|---------|
| emitir ticket / redeem | REST de emissão + resgate (uso único) |
| operador descobre sessionId via status | `GET /ndesk/tickets/{id}` devolve o `sessionId` ao criador (ADR-020) |
| operador + agente entram na sessão | `JoinSession` com JWT real (operador) e anônimo (agente) |
| **SendSignal sem consentimento → recusado** | o gate de consentimento roda **a cada** mensagem |
| consent válido | grant aceito (subconjunto do pedido) |
| relay operador→agente / agente→operador | SDP/ICE opaco é repassado ao outro lado |
| EndSession → SessionEnded | encerramento propaga |
| SendSignal após revogação → recusado | revogação tem efeito imediato |

Complementa o smoke test REST de `docs/27-executar-broker-local.md` (que não cobre o hub).

## Rodar

Com um broker de pé (ver `docs/27`):

```bash
export NDESK_BROKER_URL=http://127.0.0.1:5080
export Jwt__SigningKey=... Jwt__Issuer=remoteops Jwt__Audience=remoteops-ndesk
dotnet run -c Release --project tools/ndesk-signaling-check
```

Saída `9/9 checks OK` e código de saída `0` = tudo passou.

## Achado (por que existe)

Rodar isto de verdade expôs um bug que os testes unitários com fakes não pegavam: o Hub lia
o id do operador só do claim `sub`, mas o middleware JWT mapeia `sub` para
`ClaimTypes.NameIdentifier` (`MapInboundClaims=true`) — com um JWT real o operador era sempre
recusado no `JoinSession`. Corrigido no Hub (lê `NameIdentifier`, com `sub` de reserva, igual
aos endpoints REST) e blindado por teste de regressão em
`tests/RemoteOps.UnitTests/NDesk/NDeskSignalingHubTests.cs`.

## Descoberta de produto (resolvida)

Rodar isto também revelou que o operador não tinha como obter o `sessionId` — o
`GET /ndesk/tickets/{id}` não o devolvia, travando o fluxo real operador↔agente. **Resolvido
por `ADR-020`**: o status do ticket passa a devolver o `sessionId` ao criador. O verificador
agora o descobre por esse endpoint (sem atalho), fechando o loop de ponta a ponta.
