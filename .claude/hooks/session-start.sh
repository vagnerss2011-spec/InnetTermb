#!/usr/bin/env bash
# SessionStart hook — orienta cada sessão paralela e prepara dependências quando possível.
# Projetado para sessões web/Linux; em outras plataformas vira no-op seguro.
# Ver docs/24-orquestracao-multiagente-paralela.md.
set +e

echo "RemoteOps Suite — leia AGENTS.md e docs/24-orquestracao-multiagente-paralela.md antes de codar."
echo "Branch atual: $(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo '?')"

# Restaura dependências apenas se as ferramentas existirem (no-op caso contrário).
if command -v dotnet >/dev/null 2>&1 && ls ./*.sln >/dev/null 2>&1; then
  echo "Restaurando pacotes .NET..."
  dotnet restore >/dev/null 2>&1 || true
fi

if [ -f package.json ] && command -v npm >/dev/null 2>&1; then
  echo "Instalando dependências JS..."
  npm ci >/dev/null 2>&1 || true
fi

exit 0
