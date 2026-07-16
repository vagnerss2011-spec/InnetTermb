# Classificador de Device — Design

**Data:** 2026-07-15
**Status:** proposto (aguardando revisão do operador)
**Autor:** Vagner + Claude

## 1. Objetivo

Deixar a lista de hosts **mais visual e organizada por tipo de equipamento**. Cada host ganha:

1. Um **papel** (role) normalizado — roteador, switch, servidor Linux, OLT, etc. — que o
   sistema **sugere automaticamente** a partir do Vendor/Model (heurística local, sem rede) e o
   operador pode **confirmar ou trocar**.
2. Um **ícone**: o **logo do vendor** colado ao nome (Debian, Huawei, MikroTik, A10…), com
   fallback pra um **glifo de papel** tingido pela cor do vendor quando não houver logo.
3. **Filtro por tipo** (chips) acima da lista + coluna **"Tipo"** com o glifo de papel — pra
   navegar "só os servidores Linux", "só os roteadores Huawei", etc.

## 2. Decisões travadas (do brainstorming)

| Tema | Decisão |
|------|---------|
| Detecção | **Auto-inferir do Vendor/Model** (heurística LOCAL, sem rede); operador confirma/edita. Gancho pra detecção ativa no futuro (mesmo ponto de entrada). |
| Ícone | **Logo do vendor** colado ao nome (~16px); fallback = **glifo de papel** tingido pela cor do vendor. |
| Papel visível | Coluna **"Tipo"** (glifo + rótulo) + **chips de filtro**. (Logo sozinho não distingue roteador de switch do mesmo vendor.) |
| Organização | Chips de filtro por tipo/vendor, **ortogonais** aos grupos atuais (cliente/site). |
| Logos | Uso **interno** (sem distribuição comercial). Assets em pasta **`assets/logos/` no `.gitignore`** (o repo de código é público) — embutidos no build/instalador, não commitados. Fallback garante que nada fica sem ícone. |
| Formato do asset | **PNG** (transparente, ~48px de origem, exibido ~16px) — evita adicionar dependência de renderização de SVG ao app de credenciais. |

## 3. Arquitetura

Unidades novas, cada uma com responsabilidade única e testável isoladamente:

### 3.1 Modelo de dados — `RemoteOps.Contracts`

`Asset` ganha **um** campo:

```csharp
// Asset.cs
public string? DeviceRole { get; init; }   // normalizado: ver DeviceRoles abaixo. null = não classificado.
```

`Vendor`/`Model` permanecem como estão (o vendorKey/logo/selo são DERIVADOS deles — sem
redundância). Nova classe de constantes:

```csharp
// RemoteOps.Contracts/Assets/DeviceRoles.cs
public static class DeviceRoles
{
    public const string Router       = "router";
    public const string Switch       = "switch";
    public const string ServerLinux  = "server-linux";
    public const string ServerWindows= "server-windows";
    public const string Olt          = "olt";
    public const string Firewall     = "firewall";
    public const string LoadBalancer = "loadbalancer";
    public const string Wireless     = "wireless";
    public const string Other        = "other";
    public static readonly IReadOnlyList<string> All =
        [ Router, Switch, ServerLinux, ServerWindows, Olt, Firewall, LoadBalancer, Wireless, Other ];
}
```

### 3.2 Classificador — `RemoteOps.Desktop/Domain/DeviceClassifier.cs`

Função **pura** (sem IO), fácil de testar por matriz de entrada:

```csharp
public sealed record DeviceClassification(string Role, string? VendorKey, string? BadgeLabel, int Confidence);

public static class DeviceClassifier
{
    // Sugere role + vendorKey + selo a partir do que o operador digitou. Nunca lança; devolve
    // Role=Other/Confidence=0 quando não reconhece. NÃO acessa rede.
    public static DeviceClassification Suggest(string? vendor, string? model, string? protocol);
}
```

**Tabela de regras** (ordenada, primeira que casar vence; case-insensitive):

| Sinal (vendor/model/protocolo) | Role | VendorKey | Selo |
|---|---|---|---|
| protocolo `mikrotik`, ou vendor ~ `mikrotik`/`routeros` | router (switch se model ~ `CRS`/`CSS`) | mikrotik | ROS |
| vendor ~ `huawei` + model ~ `^(NE|ATN|AR|CX)` / `netengine` | router | huawei | VRP8 |
| vendor ~ `huawei` + model ~ `^(S\d|CE|CloudEngine)` | switch | huawei | VRP5 |
| vendor ~ `huawei` + model ~ `MA5|EA5|OLT` | olt | huawei | OLT |
| vendor/model ~ `debian` | server-linux | debian | DEB |
| vendor/model ~ `ubuntu` | server-linux | ubuntu | UBU |
| vendor/model ~ `centos|rhel|red hat|rocky|alma` | server-linux | linux | LNX |
| vendor/model ~ `windows|win server` **ou** só protocolo `rdp` | server-windows | windows | WIN |
| vendor ~ `a10` | loadbalancer | a10 | A10 |
| vendor ~ `cisco|ios|nx-os` | router (switch se model ~ `catalyst|nexus`) | cisco | CSC |
| vendor ~ `juniper|junos` | router | juniper | JNP |
| _(nada casou)_ | other | (vendor slug) | — |

A tabela vive como **lista de dados** no código (não hard-coded em `if`s espalhados) → fácil de
estender por vendor. O `BadgeLabel`/glifo só são usados no **fallback** (sem logo); com logo,
mostramos o logo.

### 3.3 Catálogo de vendor/papel — `RemoteOps.Desktop/Domain/DeviceCatalog.cs`

Mapeia `Role` → glifo (Segoe MDL2 ou Path) + rótulo pt-BR; e `VendorKey` → cor + nome de arquivo
de logo. Fonte única consumida pela UI.

```csharp
public static class DeviceCatalog
{
    public static string RoleGlyph(string? role);   // codepoint MDL2 (ou key de Path)
    public static string RoleLabel(string? role);   // "Roteador", "Switch", "Servidor Linux"…
    public static Brush  VendorColor(string? vendorKey);
    public static string? LogoFileName(string? vendorKey); // "<key>.png" ou null
}
```

### 3.4 Assets de logo

- Pasta `src/RemoteOps.Desktop/assets/logos/` (nova), **no `.gitignore`**.
- Build copia pra saída (`CopyToOutputDirectory`), instalador embute.
- App resolve `logos/<vendorKey>.png` em runtime; **se não existir → fallback pro glifo de papel**
  tingido por `VendorColor`. Isso mantém tudo funcionando mesmo sem nenhum logo instalado.
- Sourcing dos logos é passo de implementação (interno; operador aprova cada um). O repositório
  público **não** recebe os PNGs.

### 3.5 UI — `RemoteOps.Desktop/Views`

- **`DeviceIcon` (UserControl reutilizável)**: recebe `Role` + `VendorKey`; renderiza `<Image>` do
  logo se existir, senão o glifo de papel tingido. ~16px. Usado na lista e no editor.
- **Editor de host (`HostEditorDialog`)**: linha **"Tipo"** — `ComboBox` de papéis, **auto-preenchido**
  por `DeviceClassifier.Suggest` quando Vendor/Model/Protocolo mudam (operador sobrescreve), com
  preview ao vivo do `DeviceIcon` + selo.
- **Lista (`HostsView`)**: `DeviceIcon` colado ao nome; nova coluna **"Tipo"** (glifo + rótulo);
  **barra de chips** de filtro acima do DataGrid (Todos + papéis/vendors **presentes** nos dados),
  que filtram a lista (ortogonal aos grupos).

### 3.6 Persistência — `RemoteOps.Desktop/Infrastructure`

- `SqlCipherLocalStore`: coluna `device_role TEXT` na tabela `assets` (+ migração idempotente
  `ALTER TABLE ADD COLUMN` guardada por checagem de schema); SELECT/INSERT/UPDATE/mapper atualizados.
- `InMemoryLocalStore` + `AddAssetRequest` + `HostEditorViewModel`: propagam `DeviceRole`.
- **Retrocompatível**: coluna nullable; hosts antigos ficam `null` (chip "Sem tipo") até o operador
  editar/aceitar a sugestão.

## 4. Fluxo de dados

```
Cadastrar/editar host
  → operador digita Vendor/Model/Protocolo
  → DeviceClassifier.Suggest(...) preenche o ComboBox "Tipo" (editável)
  → salva Asset.DeviceRole no store
Renderizar lista
  → HostsViewModel projeta cada Asset → (DeviceRole, VendorKey via DeviceClassifier/Catalog)
  → DeviceIcon: logo <vendorKey>.png se existir, senão glifo de papel tingido
  → coluna "Tipo" (glifo+rótulo) + chips derivados dos papéis/vendors presentes
  → filtro por chip = predicado no ICollectionView da lista
```

## 5. Tratamento de erros / edge cases

- **Sem logo pro vendor** → fallback pro glifo de papel tingido (nunca fica vazio).
- **DeviceRole null** (host antigo / não reconhecido) → glifo genérico + chip "Sem tipo"; não some da lista.
- **Vendor/Model vazios** → `Suggest` devolve `other`/confidence 0; operador escolhe manualmente.
- **Migração de schema** → `ALTER TABLE` só roda se a coluna não existir (checagem via PRAGMA/schema).
- **Arquivo de logo corrompido/ilegível** → try/catch no carregamento da `Image` → fallback pro glifo.

## 6. Testes

- **`DeviceClassifierTests`** (unit, puro): matriz de entradas → `(Role, VendorKey, Badge)` esperados,
  cobrindo cada linha da tabela + casos negativos (vazio, desconhecido, protocolo isolado).
- **`DeviceCatalogTests`**: todo role tem glifo+rótulo; todo vendorKey conhecido tem cor.
- **Render STA** (`HostEditorDialogRenderTests`/novo `DeviceIconRenderTests`): editor com "Tipo"
  visível + `DeviceIcon` com e sem logo (fallback) renderizam sem lançar.
- **Filtro** (`HostsViewModelTests`): aplicar chip filtra os assets certos; "Todos" limpa; chips só
  listam papéis/vendors presentes.
- **Persistência** (`SqlCipherLocalStore`/`InMemoryLocalStore` tests): round-trip de `DeviceRole`;
  migração sobre banco sem a coluna.
- Gates de sempre: build Release limpo, `dotnet format --verify-no-changes`, suíte completa verde.

## 7. Fora de escopo (YAGNI, v1)

- Detecção **ativa** por rede (banner SSH/RouterOS/SNMP) — só o gancho (`Suggest` centraliza).
- Tipo por-endpoint (o papel é do device = Asset).
- Editor de regras/vendors na UI (a tabela é data no código, estendível em fonte).
- Campo `Platform` editável (VRP8/VRP5) separado — o selo já sai do Vendor/Model; adicionar depois se faltar.
- Logos oficiais no repo público (ficam locais/gitignored).

## 8. Arquivos afetados (resumo)

- Novo: `Contracts/Assets/DeviceRoles.cs`, `Desktop/Domain/DeviceClassifier.cs`,
  `Desktop/Domain/DeviceCatalog.cs`, `Desktop/Views/DeviceIcon.xaml(.cs)`, `assets/logos/` (gitignored).
- Alterado: `Contracts/Assets/Asset.cs`, `Desktop/Domain/AddAssetRequest.cs`,
  `Desktop/ViewModels/HostEditorViewModel.cs`, `Desktop/ViewModels/HostsViewModel.cs`,
  `Desktop/Views/HostEditorDialog.xaml`, `Desktop/Views/HostsView.xaml`,
  `Desktop/Infrastructure/InMemoryLocalStore.cs`, `Desktop/Infrastructure/SqlCipherLocalStore.cs`,
  `Themes/Tokens/Icons.xaml` (glifos de papel), `.gitignore`.
- Testes: `DeviceClassifierTests`, `DeviceCatalogTests`, `DeviceIconRenderTests`, `HostsViewModel` filtro, store round-trip/migração.
