#!/usr/bin/env bash
# Espelha uma release do repo PRIVADO (código-fonte) para o repo PÚBLICO (feed de auto-update).
#
# Contexto: o app (UpdateFeedConfig.DefaultRepoUrl) aponta o feed do Velopack para o repo público
# InnetTermb-releases, que o GithubSource lê ANONIMAMENTE — sem token, sem expor o código. O
# release.yml continua construindo/publicando no repo privado quando não há RELEASES_REPO_TOKEN;
# este script copia os artefatos de feed (RELEASES, releases.win.json, *.nupkg, Setup.exe, etc.)
# para o repo público, deixando o auto-update anônimo funcionar.
#
# Uso:   tools/mirror-release.sh vX.Y.Z
# Requer: gh autenticado com leitura no repo privado e escrita no público (owner ou PAT).
set -euo pipefail

TAG="${1:?uso: tools/mirror-release.sh vX.Y.Z}"
PRIVATE="vagnerss2011-spec/InnetTermb"
PUBLIC="vagnerss2011-spec/InnetTermb-releases"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "==> Baixando assets de $PRIVATE $TAG"
gh release download "$TAG" --repo "$PRIVATE" --dir "$TMP" --clobber

TITLE="$(gh release view "$TAG" --repo "$PRIVATE" --json name --jq '.name' 2>/dev/null || echo "RemoteOps Desktop $TAG")"
IS_PRE="$(gh release view "$TAG" --repo "$PRIVATE" --json isPrerelease --jq '.isPrerelease' 2>/dev/null || echo false)"
PRE_FLAG=()
[ "$IS_PRE" = "true" ] && PRE_FLAG=(--prerelease)

echo "==> Publicando em $PUBLIC $TAG"
if gh release view "$TAG" --repo "$PUBLIC" >/dev/null 2>&1; then
  gh release upload "$TAG" --repo "$PUBLIC" --clobber "$TMP"/*
else
  gh release create "$TAG" --repo "$PUBLIC" \
    --title "${TITLE:-RemoteOps Desktop $TAG}" \
    --notes "Feed de auto-update (espelho de $PRIVATE $TAG). O código-fonte permanece privado; aqui ficam só os instaladores e o feed do Velopack." \
    "${PRE_FLAG[@]}" "$TMP"/*
fi

echo "==> OK: feed público atualizado para $TAG"

# ── Poda: mantém no máximo 3 releases no feed público ────────────────────────────
# Ordena por SEMVER numérico (robusto — NÃO por data de criação, que se embaralha quando o
# mirror re-cria/atualiza releases; foi o que me fez apagar a versão errada uma vez). Guarda
# dupla: NUNCA apaga a que acabou de ser publicada ($TAG).
echo "==> Podando o feed (mantendo as 3 maiores versões)"
to_del="$(gh release list --repo "$PUBLIC" --json tagName \
  --jq '[.[].tagName | select(test("^v[0-9]+\\.[0-9]+\\.[0-9]+$"))]
        | sort_by(.[1:] | split(".") | map(tonumber)) | reverse | .[3:] | .[]' || true)"
for t in $to_del; do
  if [ "$t" = "$TAG" ]; then echo "  guarda: pulando release recém-publicada $t"; continue; fi
  echo "  apagando release antiga do feed: $t"
  gh release delete "$t" --repo "$PUBLIC" --cleanup-tag --yes || true
done
echo "==> Feed com no máximo 3 releases."
