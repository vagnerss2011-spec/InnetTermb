# 17 — Contratos de API

## Convenções

- JSON em camelCase.
- Datas em UTC ISO-8601.
- IDs em ULID ou GUID, escolher antes do código final.
- Erros no formato ProblemDetails.
- Toda request autenticada inclui correlation ID.

## Sync Pull

### Request

`GET /api/workspaces/{workspaceId}/sync/pull?cursor=123&limit=500`

### Response

```json
{
  "workspaceId": "01HY...",
  "fromCursor": 123,
  "toCursor": 456,
  "hasMore": false,
  "changes": [
    {
      "changeId": 456,
      "entityType": "asset",
      "entityId": "01HZ...",
      "operation": "updated",
      "version": 7,
      "patch": {
        "name": "router-borda-01",
        "tags": ["core", "ipv6"]
      },
      "actorUserId": "01HU...",
      "createdAt": "2026-06-29T12:00:00Z"
    }
  ]
}
```

## Sync Push

### Request

`POST /api/workspaces/{workspaceId}/sync/push`

```json
{
  "clientId": "device-01",
  "changes": [
    {
      "clientChangeId": "local-123",
      "entityType": "endpoint",
      "entityId": "01HZ...",
      "operation": "updated",
      "baseVersion": 4,
      "patch": {
        "ipv6": "2001:db8::10",
        "preferIpv6": true
      }
    }
  ]
}
```

### Response

```json
{
  "accepted": [
    {
      "clientChangeId": "local-123",
      "serverChangeId": 900,
      "newVersion": 5
    }
  ],
  "conflicts": []
}
```

## Asset

```json
{
  "id": "01HZ...",
  "workspaceId": "01HY...",
  "groupId": "01HG...",
  "name": "olt-pop-01",
  "vendor": "zte",
  "model": "C300",
  "site": "POP Centro",
  "tags": ["olt", "pon"],
  "version": 3,
  "endpoints": []
}
```

## Endpoint

```json
{
  "id": "01HE...",
  "assetId": "01HZ...",
  "protocol": "ssh",
  "fqdn": "olt-pop-01.local",
  "ipv4": "10.0.0.10",
  "ipv6": "2001:db8::10",
  "port": 22,
  "preferIpv6": true,
  "credentialRefId": "01HC...",
  "profile": {
    "vendorProfile": "zte-olt",
    "terminalEncoding": "utf-8"
  }
}
```

## CredentialRef

```json
{
  "id": "01HC...",
  "name": "Senha padrão OLTs",
  "type": "password",
  "scope": "group",
  "metadata": {
    "username": "admin",
    "hasPrivateKey": false,
    "lastRotatedAt": "2026-06-01T00:00:00Z"
  },
  "secretEnvelopeId": "01HS...",
  "version": 2
}
```

## AuditEvent

```json
{
  "id": "01HA...",
  "workspaceId": "01HY...",
  "actorUserId": "01HU...",
  "action": "session.ssh.opened",
  "targetType": "endpoint",
  "targetId": "01HE...",
  "ipAddress": "2001:db8::1234",
  "deviceId": "device-01",
  "metadata": {
    "protocol": "ssh",
    "port": 22,
    "credentialRefId": "01HC..."
  },
  "createdAt": "2026-06-29T12:05:00Z"
}
```

## NDesk Ticket

```json
{
  "id": "01NT...",
  "workspaceId": "01HY...",
  "createdBy": "01HU...",
  "expiresAt": "2026-06-29T12:30:00Z",
  "status": "waiting",
  "permissionsRequested": ["view", "control"],
  "linkToken": "short-lived-token"
}
```

## SignalR events

- `workspace.changed`
- `credential.rotated`
- `policy.changed`
- `ndesk.ticket.statusChanged`
- `ndesk.signal`

## External Tool Launch — WinBox

Contrato em `contracts/external-tool-launch.schema.json`.

Exemplo:

```json
{
  "id": "01WB...",
  "workspaceId": "01HY...",
  "tool": "winbox",
  "hostId": "01HE...",
  "target": {
    "address": "2001:db8::10",
    "addressFamily": "ipv6",
    "port": 8291,
    "preferIpv6": true
  },
  "login": "admin",
  "credentialRefId": "01HC...",
  "includePasswordArgument": false,
  "workspaceName": "<own>",
  "requestedBy": "01HU...",
  "requestedAt": "2026-06-29T12:05:00Z",
  "policyDecisionId": "01PD..."
}
```

## NDesk Permission Grant

Contrato em `contracts/ndesk-permission-grant.schema.json`.

Exemplo:

```json
{
  "sessionId": "01NS...",
  "ticketId": "01NT...",
  "grantedBy": {
    "displayName": "Cliente atendido",
    "windowsUser": "CLIENTE-PC\\maria",
    "machineName": "CLIENTE-PC"
  },
  "grantedAt": "2026-06-29T12:08:00Z",
  "mode": "control",
  "permissions": ["view", "control"],
  "expiresAt": "2026-06-29T12:38:00Z",
  "revokedAt": null,
  "revokedBy": null,
  "consentTextVersion": "2026-06-29.1"
}
```

## NDesk Session Telemetry

Contrato em `contracts/ndesk-session-telemetry.schema.json`.

Exemplo:

```json
{
  "sessionId": "01NS...",
  "timestamp": "2026-06-29T12:09:00Z",
  "route": "relayTcp",
  "rttMs": 85,
  "packetLossPercent": 1.2,
  "bitrateKbps": 900,
  "fpsCaptured": 12,
  "fpsDelivered": 10,
  "width": 1280,
  "height": 720,
  "codec": "h264",
  "agentCpuPercent": 22,
  "agentMemoryMb": 96,
  "qualityProfile": "lowBandwidth"
}
```

## Novos SignalR events

- `externalTool.launchRequested`
- `externalTool.launchAudited`
- `ndesk.permission.requested`
- `ndesk.permission.granted`
- `ndesk.permission.revoked`
- `ndesk.telemetry.updated`
- `ndesk.route.changed`
