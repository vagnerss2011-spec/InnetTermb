# ADR-009 — Biblioteca MVVM: CommunityToolkit.Mvvm

## Status

Aceita.

## Contexto

O ADR-001 define C#/.NET 10 + WPF + MVVM como stack principal do desktop. Para implementar
o padrão MVVM — `INotifyPropertyChanged`, commands, `ObservableObject` — existem três opções:

| Opção | Licença | Source generators | Manutenção |
|---|---|---|---|
| **CommunityToolkit.Mvvm** | MIT | ✅ (`[ObservableProperty]`, `[RelayCommand]`) | Microsoft / .NET Foundation |
| Prism.WPF | MIT | Parcial | Parcial (sem Microsoft) |
| Implementação própria | — | Não | Alto custo |

A regra do projeto (`CLAUDE.md`, item 4 e CONTRIBUTING.md) exige ADR antes de adicionar
qualquer biblioteca. Esta ADR documenta retroativamente a adição de `CommunityToolkit.Mvvm`
feita no commit `70d80d3` sem a devida aprovação — violação corrigida aqui.

## Decisão

Usar **CommunityToolkit.Mvvm** (NuGet `CommunityToolkit.Mvvm`, versão ≥ 8.3.x).

Razões:

1. MIT license, mantida pela Microsoft e .NET Foundation.
2. Source generators reduzem boilerplate de `INotifyPropertyChanged` e `ICommand` —
   menos código manual, menos bugs de notificação.
3. É a recomendação oficial da documentação .NET para WPF/MVVM moderno.
4. Sem dependências transitivas pesadas.

## Consequências positivas

- `[ObservableProperty]` elimina implementação manual de `Set` + evento.
- `[RelayCommand]` gera `ICommand` sem classe auxiliar separada.
- Testabilidade: `ObservableObject` não exige UI para testar ViewModels.

## Consequências negativas

- Adiciona dependency de source generator ao build (tempo de compilação marginal).
- Source generators podem dificultar debugging em raros cenários de geração de código.

## Restrições de uso

- Usar apenas em projetos `net10.0-windows` (WPF/Desktop).
- Projetos de contrato e adaptadores (`RemoteOps.Contracts`, `*.SSH`, `*.Telnet`) não devem
  referenciar esta lib — não há UI nesses projetos.
- Atualização de versão minor não exige revisão; major exige.
