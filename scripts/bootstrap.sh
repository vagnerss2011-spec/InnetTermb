#!/usr/bin/env bash
#
# RemoteOps Cloud — bootstrap do 1º tenant/workspace + verificação do deploy.
# Uso (na raiz do repo, no servidor):  ./scripts/bootstrap.sh
#
# POR QUE ESTE SCRIPT NÃO CRIA TENANT/WORKSPACE:
# o POST /auth/register já cria Tenant + Workspace + User + Membership numa
# transação só (AccountService.RegisterAsync) e devolve o workspaceId. Um seed que
# inserisse tenant/workspace "vazio" antes disso criaria um par ÓRFÃO: o register
# não reaproveita workspace existente, e o operador acabaria com dois workspaces e
# sincronizando no errado. Então o bootstrap real é: subir a stack → registrar a
# conta PELO APP → pegar aqui o workspaceId para o REMOTEOPS_CLOUD_WORKSPACE_ID.
#
# Este script verifica a stack e mostra esse id. Ver docs/runbook-deploy-debian.md.

set -euo pipefail

cd "$(dirname "$0")/.."

if ! docker compose version >/dev/null 2>&1; then
	echo "ERRO: 'docker compose' não encontrado. Ver docs/runbook-deploy-debian.md (pré-requisitos)." >&2
	exit 1
fi

if [ ! -f .env ]; then
	echo "ERRO: .env não existe. Rode: cp .env.example .env  e preencha os valores." >&2
	exit 1
fi

# Lê o .env do operador e aplica os MESMOS defaults do docker-compose.yml.
# shellcheck disable=SC1091
set -a; . ./.env; set +a
DB_NAME="${POSTGRES_DB:-remoteops}"
DB_USER="${POSTGRES_USER:-remoteops}"

echo "── 1/3 · Containers ───────────────────────────────────────────────"
docker compose ps

echo
echo "── 2/3 · Saúde da API ─────────────────────────────────────────────"
# De dentro da rede do compose: a porta da API não é publicada no host (só o Caddy é).
if docker compose exec -T api curl --fail --silent --show-error http://localhost:8080/health; then
	echo
	echo "OK: /health respondeu."
else
	echo >&2
	echo "ERRO: /health não respondeu. Veja: docker compose logs api" >&2
	exit 1
fi

echo
echo "── 3/3 · Tenants e workspaces ─────────────────────────────────────"
WORKSPACES=$(docker compose exec -T postgres psql -U "$DB_USER" -d "$DB_NAME" -At -F '|' -c \
	'SELECT w."Id", w."Name", t."Name", w."CreatedAt" FROM workspaces w JOIN tenants t ON t."Id" = w."TenantId" ORDER BY w."CreatedAt";')

if [ -z "$WORKSPACES" ]; then
	cat <<-'EOF'
		Nenhum workspace ainda — isto é o ESPERADO num deploy novo.

		Próximo passo: abra o RemoteOps no seu PC, aponte para este servidor e crie a
		conta (Criar conta). O /auth/register cria tenant + workspace + usuário e
		devolve o workspaceId. Depois rode este script de novo para ver o id.

		Lembrete E2EE: a senha da conta e a chave de recuperação NUNCA chegam a este
		servidor. Se as duas forem perdidas, o cofre é irrecuperável por design.
	EOF
	exit 0
fi

printf '%s\n' "$WORKSPACES" | while IFS='|' read -r id name tenant created; do
	echo "  workspace : $name  (tenant: $tenant, criado: $created)"
	echo "  id        : $id"
	echo
done

cat <<-'EOF'
	Configure o app com o id acima:
	  REMOTEOPS_CLOUD_ENABLED=true
	  REMOTEOPS_CLOUD_URL=https://<REMOTEOPS_PUBLIC_HOSTNAME>
	  REMOTEOPS_CLOUD_WORKSPACE_ID=<id>
EOF
