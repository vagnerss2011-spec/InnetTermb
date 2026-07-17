# ADR-009 — Deploy do RemoteOps.Cloud: Docker Compose + Caddy + migration no startup

## Status

Aceita

## Contexto

O backend estava completo em código e **não deployável**: sem migrations (o ADR-008
já registrava "migrations precisam ser aplicadas antes de cada deploy" como etapa de
pipeline — que nunca existiu), sem imagem, sem TLS e sem procedimento. O destino é
um servidor **Debian 13 do operador**, single-node, operado por uma pessoa só, e o
cliente **exige `https`** (spec cloud-sync-e2ee-phase1 §10).

Restrições que moldam a decisão:

- Quem opera é o próprio dono do produto, não um time de SRE. Cada passo manual é
  um passo que vai ser esquecido às 2h da manhã.
- O agente que implementa **não tem** (e não deve ter) acesso ao servidor nem às
  credenciais de produção: a entrega é artefato + runbook.
- É um produto de credenciais: superfície mínima e nada de segredo versionado.

## Decisão

### 1. Topologia: Compose de 3 serviços, só o proxy exposto

`caddy` (80/443 públicas) → `api` (8080, interna) → `postgres` (5432, interna).
Nem a API nem o Postgres publicam porta no host.

**Caddy** entre as três opções já autorizadas em `docs/10` (Caddy, Nginx, Traefik):
TLS automático via ACME sem certbot, cron de renovação, nem arquivo de vhost. É a
que tem menos peça móvel para um operador solo.

### 2. Migrations aplicadas no startup da API

`DatabaseBootstrapper.MigrateIfRelational` roda antes do primeiro request; o deploy
é `docker compose up -d --build` e nada mais. Guarda `IsRelational()` porque a suíte
sobe a API com EF InMemory (ADR-008), que não tem migrations.

### 3. Contrato de configuração com dois nomes

`REMOTEOPS_DB_CONNECTION` e `Jwt__SecretKeyBase64` (nomes do spec §9 e do runbook),
com `ConnectionStrings__Default` e `Jwt__SigningKey` mantidos como alias legado.
Resolução única em `Configuration/DeploymentConfig`, que valida e **falha no
startup** — não no primeiro login.

### 4. Sem seed de tenant/workspace

O `POST /auth/register` já cria Tenant + Workspace + User + Membership numa
transação e devolve o `workspaceId`. `scripts/bootstrap.sh` verifica a stack e
mostra o id; não insere nada.

## Consequências positivas

- Deploy de um comando; atualização (`git pull && docker compose up -d --build`)
  aplica migration nova sozinha. Sem passo de schema esquecido entre release e boot.
- Sem segredo no repositório: `.env.example` vai com campos **vazios** e o comando
  que gera cada valor; o `.env` real está no `.gitignore`. Valor de exemplo em
  template vira segredo de produção no dia em que alguém tem pressa.
- Erro de configuração vira mensagem clara no boot do container (`${VAR:?...}` no
  compose e validação no `DeploymentConfig`), não 500 intermitente em produção.
- Superfície mínima: banco e API inalcançáveis de fora, runtime não-root.
- `MigrationsTests` transforma drift de modelo em teste vermelho — o modo de falha
  que o ADR-008 previa ("aplicar antes do deploy") deixa de depender de disciplina.

## Consequências negativas

- **Migration no startup não escala para mais de uma réplica**: duas instâncias
  migrando ao mesmo tempo é corrida. Enquanto for single-node, é seguro; a segunda
  réplica exige mover a migration para um job de release (ver critérios).
- **`curl` na imagem de runtime** só para o HEALTHCHECK (a imagem `aspnet` não traz
  cliente HTTP). É um binário a mais num container de produto de credenciais.
- **Caddy depende de DNS público + porta 80** para o desafio ACME. Deploy em rede
  fechada precisa de outro caminho (DNS-01 ou certificado manual).
- **Dockerfile builda o projeto, não a solution** (`RemoteOps.Desktop` é WPF e não
  restaura em Linux): um projeto novo que a API venha a referenciar precisa ser
  copiado explicitamente no Dockerfile.
- **Rate limit do `/auth` depende do IP do cliente atrás do proxy.** A API confia no
  `X-Forwarded-For` do Caddy via `UseForwardedHeaders` explícito no `Program.cs` (antes
  do rate-limiter), com `ForwardedHeadersSetup` confiando SÓ nas faixas das bridges do
  Docker (`172.16.0.0/12` + `10.0.0.0/8`, override por `TRUSTED_PROXY_CIDR`) e `ForwardLimit=1`.
  Fora dessas faixas o header é ignorado (anti-spoof). **Correção de revisão adversarial:**
  a abordagem anterior (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` sem configurar a confiança)
  degradava silenciosamente — o default do middleware só confia em loopback, e o Caddy nunca
  é loopback na bridge do Compose, então o header era IGNORADO e o rate limit virava um balde
  GLOBAL de 20/min (DoS de toda a auth) com os logs de IP registrando só o proxy. Não ligue
  aquela env var junto com o middleware explícito: processaria o header duas vezes.
- Backup/restore continua manual (Fase 5), documentado no runbook.

## Alternativas consideradas

| Alternativa | Motivo da rejeição |
|---|---|
| Nginx + certbot | Mais peças (vhost, cron de renovação, reload); ganho zero no MVP |
| Traefik | Descoberta por labels é ótima com muitos serviços; aqui são três |
| `dotnet publish` + systemd, sem container | Exige SDK e runtime no host, e o Postgres vira instalação manual |
| Migration por job separado (`docker compose run migrate`) | Correto para multi-réplica, mas adiciona passo manual que o operador solo esquece — reavaliar junto com a 2ª réplica |
| `EnsureCreated()` no lugar de migrations | Não versiona schema; impossível evoluir o banco sem recriar |
| Seed do 1º tenant/workspace | Criaria par órfão: o `/auth/register` não reaproveita workspace existente |
| Publicar 5432 para administrar o banco de fora | Expõe o Postgres à internet; `docker compose exec` resolve |
| Imagem com healthcheck sem `curl` | Sem cliente HTTP não há healthcheck; o Caddy mandaria tráfego para API ainda migrando |

## Critérios de revisão

- **Ao adicionar a 2ª réplica da API**: mover a migration do startup para job de
  release e reavaliar esta decisão (é a consequência negativa mais séria).
- Se o `/auth/register` aberto virar problema antes da Fase 4, trocar a mitigação
  por Caddy (runbook §9) por convite/allowlist no próprio backend.
- Se o deploy passar a exigir rede fechada/sem DNS público, trocar o ACME HTTP-01
  por DNS-01 ou certificado gerenciado.
- Se surgir mais de um ambiente (staging), extrair os valores do compose para
  overlays em vez de multiplicar `.env`.
