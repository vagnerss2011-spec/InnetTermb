# Runbook — Deploy do RemoteOps Cloud no Debian 13

> **Para o operador.** Passo a passo para subir o backend de sync no seu servidor
> Debian 13. Todos os comandos rodam como **root** (entre com `su -`).
>
> **Tempo estimado:** ~15 minutos, sendo a maior parte esperando o build da imagem.
>
> Referências: `docs/10-backend-cloud-sync.md` (arquitetura),
> `docs/superpowers/specs/2026-07-16-cloud-sync-e2ee-phase1-design.md` (modelo E2EE).

---

## 0. O que você vai subir

Três containers, orquestrados por um `docker-compose.yml`:

| Container | Papel | Exposto na internet? |
|---|---|---|
| `caddy` | Proxy reverso + certificado TLS automático | **Sim** — portas 80 e 443 |
| `api` | O backend (ASP.NET Core) | Não — só o Caddy alcança |
| `postgres` | Banco de dados | Não — só a API alcança |

Dois pontos que valem entender antes:

- **A API aplica as migrations sozinha no startup.** Não existe passo manual de
  criação de schema. Banco novo = schema criado no primeiro boot.
- **O servidor não vê nada em claro (E2EE).** Senha da conta, chave mestra e as
  senhas dos equipamentos **nunca** chegam aqui — o servidor guarda só blobs
  cifrados. Consequência direta: **se você perder a senha da conta E a chave de
  recuperação, o cofre é irrecuperável.** Não existe "resetar senha" que devolva
  os dados. Guarde a chave de recuperação fora da máquina.

---

## 1. Pré-requisitos

### 1.1 Servidor

- Debian 13, com IP público.
- Portas **80** e **443** liberadas na internet (firewall e, se houver, o security
  group do provedor). A 80 não é opcional: é por ela que o Let's Encrypt valida o
  domínio.
- **Um nome DNS** (ex.: `cloud.suaempresa.com.br`) com registro `A` apontando para
  o IP deste servidor. Confira antes de continuar:

```bash
dig +short cloud.suaempresa.com.br
# tem que devolver o IP do servidor
```

> Sem DNS não há TLS automático, e sem TLS o app não conecta (o cliente exige
> `https`). Se o DNS ainda não propagou, espere — não adianta subir o Caddy antes.

### 1.2 Docker

```bash
apt update
apt install -y ca-certificates curl git
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo "$VERSION_CODENAME") stable" > /etc/apt/sources.list.d/docker.list
apt update
apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

Confira:

```bash
docker compose version
```

---

## 2. Clonar o repositório

```bash
mkdir -p /opt
cd /opt
git clone <URL_DO_REPO> remoteops-cloud
cd remoteops-cloud
git checkout feature/cloud-backend
```

---

## 3. Gerar os segredos e preencher o `.env`

```bash
cp .env.example .env
```

Gere cada valor **neste servidor** e cole no `.env`:

```bash
# Senha do Postgres
openssl rand -base64 24

# Chave de assinatura do JWT (mínimo 32 bytes decodificados — a API recusa subir se for menor)
openssl rand -base64 32
```

Edite o `.env`:

```bash
nano .env
```

Preencha:

| Variável | Valor |
|---|---|
| `POSTGRES_PASSWORD` | o `openssl rand -base64 24` de cima |
| `JWT_SECRET_KEY_BASE64` | o `openssl rand -base64 32` de cima |
| `REMOTEOPS_PUBLIC_HOSTNAME` | seu DNS, **sem** `https://` (ex.: `cloud.suaempresa.com.br`) |
| `ACME_EMAIL` | seu e-mail (o Let's Encrypt avisa de problema no certificado) |

Proteja o arquivo — ele tem a senha do banco e a chave do JWT:

```bash
chmod 600 .env
```

> O `.env` está no `.gitignore`: ele nunca vai para o repositório. Faça backup dele
> **separado** (gerenciador de senhas), fora do servidor. Perder o
> `JWT_SECRET_KEY_BASE64` não perde dados — só desloga todo mundo. Perder o
> `POSTGRES_PASSWORD` com o volume vivo é pior: a senha ficou gravada no initdb.

---

## 4. Subir a stack

```bash
docker compose up -d --build
```

O primeiro build leva alguns minutos (compila a API dentro do container). Acompanhe:

```bash
docker compose ps
docker compose logs -f api
```

Você quer ver, no log da `api`, a migration sendo aplicada:

```
Aplicando 1 migration(s): 20260716113525_InitialCreate
Migrations aplicadas.
```

E o Caddy tirando o certificado:

```bash
docker compose logs caddy | grep -i certificate
```

---

## 5. Verificar

### 5.1 De dentro do servidor

```bash
./scripts/bootstrap.sh
```

O script confere os containers, bate no `/health` e lista os workspaces (na
primeira vez, nenhum — é o esperado).

### 5.2 De fora (é o que o app vai fazer)

```bash
curl https://cloud.suaempresa.com.br/health
# {"status":"healthy"}
```

Se isso responder com certificado válido, o servidor está pronto.

---

## 6. Criar a conta e apontar o app

**A conta é criada pelo app, não aqui.** O `POST /auth/register` cria tenant +
workspace + usuário numa transação só e devolve o `workspaceId` — por isso não
existe script de seed (um tenant/workspace pré-criado ficaria órfão, o register não
reaproveita).

1. No seu PC, configure o RemoteOps:

   ```
   REMOTEOPS_CLOUD_ENABLED=true
   REMOTEOPS_CLOUD_URL=https://cloud.suaempresa.com.br
   ```

2. Abra o app e use **Criar conta** (e-mail + senha).
3. **Guarde a chave de recuperação** que aparece uma única vez. Sem ela e sem a
   senha, o cofre não volta.
4. De volta no servidor, pegue o id do workspace:

   ```bash
   ./scripts/bootstrap.sh
   ```

5. Complete a configuração do app:

   ```
   REMOTEOPS_CLOUD_WORKSPACE_ID=<o id que o script mostrou>
   ```

Nos próximos PCs: instale o app, aponte para a mesma URL e use **Entrar** — os
dados e as senhas dos equipamentos descem e são decifrados localmente.

---

## 7. Operação do dia a dia

```bash
# Status
docker compose ps

# Logs
docker compose logs -f api
docker compose logs -f caddy

# Reiniciar só a API
docker compose restart api

# Atualizar para uma versão nova do backend
git pull
docker compose up -d --build      # migrations novas aplicam sozinhas no startup

# Parar tudo (os dados ficam no volume)
docker compose down

# Parar e APAGAR os dados (irreversível)
docker compose down -v
```

---

## 8. Troubleshooting

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| `api` em restart loop, log diz `Banco não configurado` | `.env` sem `POSTGRES_PASSWORD` ou `.env` no diretório errado | Rode `docker compose config` na raiz do repo; ele aborta dizendo qual variável falta |
| `api` sobe e morre: `Jwt__SecretKeyBase64 precisa ter no mínimo 32 bytes` | chave gerada com `-base64 16` ou colada pela metade | Gere de novo com `openssl rand -base64 32` e `docker compose up -d` |
| `Jwt__SecretKeyBase64 não é base64 válido` | valor com aspas, espaço ou quebra de linha no `.env` | O valor vai **sem aspas**, numa linha só |
| Certificado não sai; log do Caddy fala em ACME/timeout | DNS não aponta pra cá, ou porta 80 fechada | `dig +short <host>` e confira o firewall; a 80 é obrigatória pro desafio |
| `curl https://...` dá 502 | a API ainda está subindo/migrando, ou caiu | `docker compose ps` (a API tem healthcheck) e `docker compose logs api` |
| Login/registro devolvendo **429** cedo demais | rate limit do `/auth` particiona por IP; se todos chegarem com o IP do proxy, o balde vira global | Confirme `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` na `api` e o `header_up X-Forwarded-For` no `deploy/Caddyfile` |
| `docker compose up` reclama de variável não definida | é de propósito: `${VAR:?...}` aborta em vez de subir com default fraco | Preencha a variável que a mensagem citou no `.env` |

Log da API sem segredo por design (senha, token e AuthHash nunca são logados) —
pode colar o log num chamado.

---

## 9. Segurança — leia antes de expor

- **O `/auth/register` está aberto.** Qualquer um que alcance o servidor pode criar
  uma conta (e um tenant) nele. Enquanto não existir convite/allowlist (pendência
  registrada da Fase 1), restrinja por fora enquanto você é o único usuário — por
  exemplo, no `deploy/Caddyfile`:

  ```
  @registro path /auth/register
  handle @registro {
      @naoAutorizado not remote_ip <SEU_IP>/32
      respond @naoAutorizado 403
      reverse_proxy api:8080
  }
  ```

  Depois de criar sua conta, você pode simplesmente responder `403` para
  `/auth/register` e liberar de novo só quando for cadastrar alguém.
- Postgres e API **não** publicam porta no host. Não adicione `ports:` neles.
- Mantenha o `.env` em `chmod 600` e fora de backup compartilhado.
- Atualize as imagens de tempos em tempos: `docker compose pull && docker compose up -d`.

---

## 10. Backup — fase posterior (o que fazer enquanto isso)

**Backup/restore automatizado do Postgres é da Fase 5** (replicação de backup
cifrado). Não existe nada automático neste deploy hoje. Enquanto isso, o mínimo
manual:

```bash
# Dump (rode antes de qualquer atualização)
docker compose exec -T postgres pg_dump -U remoteops remoteops | gzip > /root/remoteops-$(date +%F).sql.gz

# Restore num banco vazio
gunzip -c /root/remoteops-2026-07-16.sql.gz | docker compose exec -T postgres psql -U remoteops -d remoteops
```

Guarde o dump **fora do servidor**. Ele contém os blobs cifrados: sem a senha da
conta eles não abrem, mas ainda são os seus dados — trate como sensível.

> Lembre: o dump **não** salva sua senha nem a chave de recuperação (elas nunca
> estiveram aqui). Backup do servidor não substitui guardar a chave de recuperação.
