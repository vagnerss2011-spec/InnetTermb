#!/usr/bin/env bash
# PreToolUse(Bash) hook — safety net que bloqueia comandos claramente destrutivos.
# Recebe o payload do tool em stdin (JSON) e sai com código 2 para bloquear.
# Conservador de propósito: não bloqueia limpezas normais como "rm -rf build".
input="$(cat)"

block() {
  echo "RemoteOps: comando destrutivo bloqueado pelo hook de segurança do projeto." >&2
  exit 2
}

# rm -rf na raiz, home ou wildcard perigoso
printf '%s' "$input" | grep -Eq 'rm[[:space:]]+-[a-z]*rf?[a-z]*[[:space:]]+(/|~|\*|\$HOME|\$\{HOME\})' && block

# fork bomb
printf '%s' "$input" | grep -Eq ':\(\)\{[[:space:]]*:\|:&[[:space:]]*\};:' && block

# PowerShell remoção recursiva forçada
printf '%s' "$input" | grep -Eq 'Remove-Item[^\n]*-Recurse[^\n]*-Force' && block

# git force-push para main/master (qualquer ordem de argumentos)
if printf '%s' "$input" | grep -Eq 'git[[:space:]]+push' \
   && printf '%s' "$input" | grep -Eq '(--force([^-]|$)|--force-with-lease|[[:space:]]-f([[:space:]]|$))' \
   && printf '%s' "$input" | grep -Eq '\b(main|master)\b'; then
  block
fi

exit 0
