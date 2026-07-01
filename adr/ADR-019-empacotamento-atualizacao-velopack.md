# ADR-019 — Empacotamento e atualização do Desktop via Velopack

## Status

Aceita.

## Contexto

O `RemoteOps.Desktop` (C#/.NET 10 + WPF, `docs/11-devops-github-ci.md` §Releases) ainda não tem
mecanismo de empacotamento nem de atualização automática. `docs/11` já registrava a lacuna:
*"Installer MSIX/MSI/Velopack a decidir em ADR"*. Este ADR resolve essa decisão.

Requisitos de produto para a primeira versão:

- Instalador tradicional (`Setup.exe`) para instalação padrão em máquina corporativa.
- Versão **portátil** (zip/exe sem instalação), para operadores que não têm permissão de instalar
  software ou que rodam a partir de um pendrive/compartilhamento.
- **Delta updates**: atualizações incrementais, não o pacote completo a cada versão, dado que o
  Desktop carrega WebView2/MSTSCAX/DI e tende a crescer de tamanho.
- **Atualização sob demanda**: o operador (ou a aplicação, em background) pode verificar se há
  versão nova e escolher baixar/aplicar — nunca download silencioso sem indicação visível.
- **Atualização forçada**: quando a versão mínima exigida (definida por config/feed) for maior que
  a versão instalada, o uso do app deve ser bloqueado até atualizar — controle necessário para
  forçar migração de versões com correção de segurança ou mudança de contrato incompatível.

O time já foi mordido duas vezes por adotar dependência sem verificar a licença em fonte primária
antes de decidir (`ADR-015`/`SPIKE-016`: RustDesk é AGPL-3.0, incompatível com o modelo de negócio;
`ADR-017`/`SPIKE-017`: o `LICENSE.md` oficial do SIPSorcery tem uma cláusula BDS de restrição
geopolítica de campo de uso, apesar do BSD-3-Clause declarado). Este ADR aplica o mesmo padrão de
verificação adversarial de licença em fonte primária antes de adotar Velopack.

## Verificação de licença (fonte primária)

- Arquivo `LICENSE` do repositório oficial `velopack/velopack`, obtido diretamente de
  `https://raw.githubusercontent.com/velopack/velopack/master/LICENSE`: **MIT License**, sem
  cláusula adicional. Permissões amplas ("use, copy, modify, merge, publish, distribute,
  sublicense, and/or sell"), única obrigação é manter o aviso de copyright/licença nas cópias, sem
  copyleft e sem restrição de campo de uso — ao contrário do SIPSorcery (`ADR-017`).
- Página do repositório (`github.com/velopack/velopack`) confirma o badge MIT e o pacote NuGet
  principal `Velopack`.
- `docs.velopack.io` (documentação oficial) não menciona nenhum tier pago, plano comercial ou
  serviço "Vpk.Cloud" obrigatório para operar o `vpk` CLI, o `UpdateManager` ou hospedar updates —
  hospedagem "em qualquer lugar que sirva arquivos estáticos, ex.: cloud file storage, GitHub
  Releases" é tratada como funcionalidade padrão, não paga.
- Conclusão: **licença permissiva confirmada, sem os padrões de risco (copyleft de rede, restrição
  geopolítica, dependência de serviço pago) que já desqualificaram RustDesk e SIPSorcery.** Velopack
  é aprovado para adoção sob este critério.

## Decisão

Adotar **Velopack** (pacote NuGet `Velopack`, CLI `vpk`) como framework de empacotamento e
atualização do `RemoteOps.Desktop`, alvo Windows 10/11, .NET 10 self-contained.

### 1. Modelo de artefatos

Publish self-contained (`dotnet publish -c Release -r win-x64 --self-contained true`) seguido de
`vpk pack` gera, por release:

- `RemoteOpsDesktop-<canal>-Setup.exe` (ex.: `RemoteOpsDesktop-win-Setup.exe`, canal padrão
  `win`) — instalador padrão (caminho principal, instala e registra atualização automática).
  Nome confirmado rodando `vpk pack` localmente contra um publish real deste projeto.
- `RemoteOpsDesktop-<versão>-full.nupkg` — pacote completo, usado como baseline de delta.
- `RemoteOpsDesktop-<versão>-delta.nupkg` — pacote incremental em relação à versão anterior
  publicada no mesmo canal (gerado automaticamente pelo `vpk pack` quando há uma release anterior
  no `--outputDir`/feed).
- `RemoteOpsDesktop-<canal>-Portable.zip` — versão portátil, sem instalação; roda o `.exe` extraído
  diretamente. **Não recebe atualização automática via Velopack** (é um snapshot; operador que
  precisa de auto-update deve usar o `Setup.exe`) — registrado como limitação conhecida, não como
  lacuna a resolver agora.
- `releases.<canal>.json` — índice de releases consumido pelo `UpdateManager` (sob demanda) e pelo
  suporte nativo a delta do Velopack.

### 2. Atualização sob demanda

`UpdateService` (Desktop) expõe verificação explícita (`CheckForUpdatesAsync`) que retorna a versão
disponível sem baixar nada. Download e aplicação (`DownloadUpdatesAsync`/
`ApplyUpdatesAndRestart`) só ocorrem mediante ação explícita do operador (botão "Atualizar agora")
ou de uma rotina de fundo que, ao encontrar versão nova, **notifica visivelmente** — nunca baixa ou
aplica em silêncio. Isso está alinhado ao princípio obrigatório do `CLAUDE.md` contra atualização
oculta.

### 3. Atualização forçada

O feed/config carrega um campo `minimumRequiredVersion`. Se `versão instalada < minimumRequiredVersion`,
o `UpdateService` reporta um gate de bloqueio (`UpdatePolicyResult.MustUpdate = true`) e a camada de
apresentação deve impedir o uso normal do app até a atualização ser baixada e aplicada — prompt
obrigatório e visível, sem opção de "lembrar depois". Este é o único caso em que o download pode ser
iniciado sem clique adicional do operador além de confirmar o prompt, porque a política já é, por
definição, mandatória.

### 4. Feed / hospedagem

GitHub Releases como fonte do feed — suporte nativo do Velopack (`vpk upload github`, `GithubSource`
no `UpdateManager`), sem infraestrutura própria a manter. Nenhum token de acesso é embutido em
código-fonte, build ou config versionada: repositórios de release públicos não exigem token; se um
repositório privado for usado no futuro, o token de leitura (`GithubSource` aceita um `accessToken`
opcional) deve vir de variável de ambiente/GitHub Environment no momento da build de release, nunca
hardcoded — mesma regra já aplicada a `WINBOX_SHA256`/`WINBOX_EXE_PATH` em `AppCompositionRoot`.

### 5. Fora de escopo (frente separada)

- **Assinatura de código** do `Setup.exe`/`vpk` — frente separada, não resolvida por este ADR.
  Velopack suporta assinatura via `--signParams`/`--signTemplate` no `vpk pack`, a ligar quando a
  frente de assinatura estiver pronta.
- Publicação automática de release no CI (job de release) — este ADR cobre o comando local `vpk
  pack`/`vpk upload`; automação de pipeline fica para uma PR de DevOps subsequente.

## Consequências positivas

- Licença permissiva (MIT) verificada em fonte primária, sem o risco jurídico já identificado em
  RustDesk/SIPSorcery.
- Suporte nativo a delta updates reduz banda e tempo de atualização em relação a reenviar o pacote
  completo a cada versão.
- Suporte nativo a GitHub Releases elimina a necessidade de operar um servidor de feed próprio no
  MVP.
- Modelo único (instalador + portátil) cobre os dois perfis de operador identificados sem duplicar
  lógica de empacotamento.
- `VelopackApp.Build().Run()` e `UpdateManager` são API gerenciada (.NET), consistente com o resto
  do Desktop — sem introduzir toolchain nativa nova (diferente do NDesk, `ADR-017`).

## Consequências negativas

- Versão portátil não recebe atualização automática — operador nesse modo precisa baixar
  manualmente uma nova versão quando necessário; aceito como trade-off do modelo portátil (sem
  instalação = sem processo de update residente).
- Delta updates exigem manter as releases anteriores acessíveis no feed (Velopack varre releases
  anteriores do GitHub para montar a cadeia de deltas); descontinuar/apagar releases antigas quebra
  a geração de delta para quem está muito atrasado — quem estiver assim recebe o pacote completo
  como fallback, mas isso deve ser documentado operacionalmente.
- Gate de atualização forçada introduz um novo estado de bloqueio de UI que precisa de tratamento
  explícito em todo fluxo de inicialização do Desktop (não é só "avisar e seguir") — risco de UX
  ruim se o prompt não deixar claro por que o app está bloqueado.
- Token de GitHub para repositório privado (se adotado no futuro) exige processo de rotação e
  armazenamento seguro em CI — não resolvido por este ADR porque hoje não há necessidade (repositório
  de releases pode ser público sem expor código-fonte).

## Alternativas consideradas

1. **MSIX** — rejeitada para o MVP: exige assinatura/certificado e, para instalação fora da Microsoft
   Store, configuração adicional de confiança no Windows (sideloading) mais pesada que o objetivo
   atual; sem suporte nativo a delta updates equivalente ao Velopack.
2. **MSI tradicional (WiX)** — rejeitada para o MVP: exige manter scripts WiX/Burn e não tem
   mecanismo de auto-update embutido; teria que ser combinado com uma solução de update separada
   (ex.: Squirrel.Windows, hoje descontinuado — Velopack é o sucessor espiritual direto).
3. **Squirrel.Windows** — rejeitada: projeto original está sem manutenção ativa; Velopack é
   escrito pelos mesmos princípios (mesmo formato `RELEASES`/`.nupkg` para compatibilidade de
   migração) mas ativamente mantido, multiplataforma e com CLI (`vpk`) mais simples.
4. **Servidor de update próprio (ASP.NET Core) em vez de GitHub Releases** — rejeitada para o MVP:
   adiciona infraestrutura e superfície de operação sem necessidade comprovada; GitHub Releases já
   cobre hospedagem de artefato, versionamento e é suportado nativamente pelo Velopack. Pode ser
   revisitado se o modelo de distribuição deixar de ser compatível com repositório de release
   público/privado no GitHub.

## Critérios de revisão

Revisar este ADR se:

- o Velopack mudar de licença (MIT → algo restritivo) em uma versão futura — reavaliar sob o mesmo
  critério de verificação em fonte primária aplicado aqui;
- a necessidade de assinatura de código tornar o fluxo `vpk pack` local insuficiente, exigindo mover
  o empacotamento inteiramente para CI antes do previsto;
- o modelo portátil precisar de auto-update (hoje fora de escopo) — reabre a decisão de item 1;
- o repositório de releases do GitHub precisar ser privado por política de segurança — reabre a
  decisão de hospedagem de token do item 4;
- o volume de releases tornar a varredura de deltas do Velopack sobre o histórico do GitHub lenta ou
  não confiável o suficiente, exigindo um índice de release próprio.

## Referências

- `docs/11-devops-github-ci.md` §Releases — lacuna original ("Installer MSIX/MSI/Velopack a decidir
  em ADR") resolvida por este ADR.
- `docs/23-governanca-versionamento-changelog.md` — regras de changelog/versionamento aplicadas às
  releases geradas por este mecanismo.
- `VERSIONING.md` — esquema SemVer usado como `--packVersion`.
- `adr/ADR-015-ndesk-buy-vs-build.md`, `adr/ADR-017-ndesk-stack-transporte-midia.md` — precedente do
  padrão de verificação adversarial de licença em fonte primária aplicado aqui.
- `https://raw.githubusercontent.com/velopack/velopack/master/LICENSE` — texto integral da licença
  MIT verificada.
- `https://docs.velopack.io/` — documentação oficial (packaging, distributing, API C#).
