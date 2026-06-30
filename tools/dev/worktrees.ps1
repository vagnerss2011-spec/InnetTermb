<#
.SYNOPSIS
  worktrees.ps1 — cria/remove git worktrees por frente de trabalho do RemoteOps Suite (Windows).
  Equivalente PowerShell de tools/dev/worktrees.sh. Ver docs/24-orquestracao-multiagente-paralela.md.

.EXAMPLE
  pwsh tools/dev/worktrees.ps1 list
  pwsh tools/dev/worktrees.ps1 add terminal
  pwsh tools/dev/worktrees.ps1 add-all
  pwsh tools/dev/worktrees.ps1 remove terminal
#>
param(
  [Parameter(Position = 0)] [string] $Action = 'list',
  [Parameter(Position = 1)] [string] $Front,
  [switch] $Force
)

$ErrorActionPreference = 'Stop'
$BaseBranch = if ($env:BASE_BRANCH) { $env:BASE_BRANCH } else { 'main' }
$RepoRoot   = (git rev-parse --show-toplevel).Trim()
$ParentDir  = Split-Path -Parent $RepoRoot

$Branches = [ordered]@{
  contracts    = 'feature/contracts-skeleton'
  security     = 'feature/security-vault'
  desktop      = 'feature/desktop-shell'
  sync         = 'feature/sync-local'
  terminal     = 'feature/terminal-ssh-telnet'
  mikrotik     = 'feature/mikrotik-winbox'
  cloud        = 'feature/cloud-backend'
  rdp          = 'feature/rdp-activex'
  'ndesk-viewer' = 'feature/ndesk-viewer'
  'ndesk-agent'  = 'feature/ndesk-agent-win32'
  'ndesk-relay'  = 'feature/ndesk-relay'
}

function Get-WorktreeDir([string]$f) { Join-Path $ParentDir "remoteops-$f" }

function Add-Front([string]$f) {
  if (-not $Branches.Contains($f)) { throw "Frente inválida: $f" }
  $branch = $Branches[$f]
  $dir    = Get-WorktreeDir $f
  if (Test-Path $dir) { Write-Host "Worktree já existe: $dir"; return }

  git fetch origin $BaseBranch --quiet 2>$null
  $localExists  = (git show-ref --verify --quiet "refs/heads/$branch"; $LASTEXITCODE -eq 0)
  $remoteExists = (git ls-remote --exit-code --heads origin $branch 2>$null; $LASTEXITCODE -eq 0)

  if ($localExists) {
    git worktree add $dir $branch
  } elseif ($remoteExists) {
    git worktree add $dir -b $branch --track "origin/$branch"
  } else {
    git worktree add $dir -b $branch "origin/$BaseBranch"
  }
  Write-Host "OK  frente '$f'  ->  $dir  (branch $branch)"
}

function Remove-Front([string]$f) {
  $dir = Get-WorktreeDir $f
  if (-not (Test-Path $dir)) { Write-Host "Nada para remover: $dir não existe"; return }
  if ($Force) { git worktree remove $dir --force } else { git worktree remove $dir }
  Write-Host "Removido: $dir"
}

switch ($Action) {
  'list'    { Write-Host ("Frentes: " + ($Branches.Keys -join ' ')); Write-Host ''; git worktree list }
  'add'     { if (-not $Front) { throw 'uso: add <frente>' }; Add-Front $Front }
  'add-all' { foreach ($k in $Branches.Keys) { Add-Front $k } }
  'remove'  { if (-not $Front) { throw 'uso: remove <frente> [-Force]' }; Remove-Front $Front }
  default   { throw "Ação desconhecida: $Action (use list|add|add-all|remove)" }
}
