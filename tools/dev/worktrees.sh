#!/usr/bin/env bash
#
# worktrees.sh — cria/remove git worktrees por frente de trabalho do RemoteOps Suite.
# Cada frente vira uma pasta irmã do repositório, em sua própria branch feature/*.
# Ver docs/24-orquestracao-multiagente-paralela.md.
#
# Uso:
#   bash tools/dev/worktrees.sh list
#   bash tools/dev/worktrees.sh add <frente>
#   bash tools/dev/worktrees.sh add-all
#   bash tools/dev/worktrees.sh remove <frente>
#
# Frentes válidas: contracts security desktop sync terminal mikrotik cloud rdp \
#                  ndesk-viewer ndesk-agent ndesk-relay
#
set -euo pipefail

BASE_BRANCH="${BASE_BRANCH:-main}"
REPO_ROOT="$(git rev-parse --show-toplevel)"
PARENT_DIR="$(dirname "$REPO_ROOT")"

# frente -> branch
branch_for() {
  case "$1" in
    contracts)     echo "feature/contracts-skeleton" ;;
    security)      echo "feature/security-vault" ;;
    desktop)       echo "feature/desktop-shell" ;;
    sync)          echo "feature/sync-local" ;;
    terminal)      echo "feature/terminal-ssh-telnet" ;;
    mikrotik)      echo "feature/mikrotik-winbox" ;;
    cloud)         echo "feature/cloud-backend" ;;
    rdp)           echo "feature/rdp-activex" ;;
    ndesk-viewer)  echo "feature/ndesk-viewer" ;;
    ndesk-agent)   echo "feature/ndesk-agent-win32" ;;
    ndesk-relay)   echo "feature/ndesk-relay" ;;
    *) return 1 ;;
  esac
}

ALL_FRONTS="contracts security desktop sync terminal mikrotik cloud rdp ndesk-viewer ndesk-agent ndesk-relay"

worktree_dir() { echo "$PARENT_DIR/remoteops-$1"; }

cmd_add() {
  local front="$1"
  local branch dir
  branch="$(branch_for "$front")" || { echo "Frente inválida: $front" >&2; exit 1; }
  dir="$(worktree_dir "$front")"

  if [ -d "$dir" ]; then
    echo "Worktree já existe: $dir"
    return 0
  fi

  git fetch origin "$BASE_BRANCH" --quiet || true

  # Cria a branch a partir de origin/<base> se ainda não existir; senão reaproveita.
  if git show-ref --verify --quiet "refs/heads/$branch"; then
    git worktree add "$dir" "$branch"
  elif git ls-remote --exit-code --heads origin "$branch" >/dev/null 2>&1; then
    git worktree add "$dir" -b "$branch" --track "origin/$branch"
  else
    git worktree add "$dir" -b "$branch" "origin/$BASE_BRANCH"
  fi

  echo "OK  frente '$front'  ->  $dir  (branch $branch)"
}

cmd_remove() {
  local front="$1"
  local dir
  dir="$(worktree_dir "$front")"
  if [ ! -d "$dir" ]; then
    echo "Nada para remover: $dir não existe"
    return 0
  fi
  git worktree remove "$dir" "${2:-}"
  echo "Removido: $dir"
}

cmd_list() {
  echo "Frentes disponíveis: $ALL_FRONTS"
  echo
  echo "Worktrees ativos:"
  git worktree list
}

main() {
  local action="${1:-list}"
  case "$action" in
    list)    cmd_list ;;
    add)     [ $# -ge 2 ] || { echo "uso: add <frente>" >&2; exit 1; }; cmd_add "$2" ;;
    add-all) for f in $ALL_FRONTS; do cmd_add "$f"; done ;;
    remove)  [ $# -ge 2 ] || { echo "uso: remove <frente> [--force]" >&2; exit 1; }; cmd_remove "$2" "${3:-}" ;;
    *) echo "Ação desconhecida: $action (use list|add|add-all|remove)" >&2; exit 1 ;;
  esac
}

main "$@"
