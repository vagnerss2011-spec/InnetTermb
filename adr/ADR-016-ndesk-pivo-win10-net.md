# ADR-016 — NDesk: pivô de plataforma para Windows 10/11 e agente temporário .NET moderno

## Status

Aceita. **Supera `ADR-007`** (agente legado Win32/C++). **Revisa `ADR-005`** (reabre a escolha de stack de transporte) e **`ADR-015`** (reavalia buy-vs-build sob o novo critério de reversão que este ADR dispara — conclusão mantida: "construir").

## Contexto

O produto RemoteOps decidiu **remover o suporte a Windows 7 (e Windows 8/8.1) do módulo NDesk por ora**. O alvo passa a ser exclusivamente **Windows 10 e Windows 11**, com um roadmap futuro (não comprometido neste ADR) para Linux e macOS.

Essa mudança reverte a premissa central da `ADR-007`: o agente temporário Win32/C++ nativo foi escolhido especificamente para rodar em Windows 7 SP1 sem Java, WebView2 ou .NET moderno (`docs/09-acesso-remoto-ndesk.md`, `docs/22-ndesk-performance-legacy-windows.md`). Sem o requisito Windows 7, essa restrição de toolchain deixa de existir — inclusive o risco identificado no `SPIKE-016`/`ADR-015` de que o Visual Studio 2026 removeu Windows 7 como plataforma de deployment suportada, forçando dependência do VS2022 (mainstream até ~jan/2027). Esse risco é **eliminado**, não mitigado, por este pivô.

A remoção de Windows 7 também é, textualmente, o gatilho do **critério de reversão** já registrado em `ADR-015`:

> "Revisar este ADR se: o requisito de suporte a Windows 7 SP1 for removido do roadmap do produto (enfraquece o argumento decisivo contra MeshCentral/RustDesk nesse critério, embora os critérios de licença/consentimento continuem valendo para RustDesk)"

Este ADR formaliza essa revisão (ver seção "Reavaliação buy-vs-build" abaixo) como parte da mesma decisão, em vez de abrir uma ADR-017 separada só para isso — a reavaliação é uma consequência direta, não uma pergunta nova.

Contratos já desenhados (`contracts/ndesk-ticket.schema.json`, `contracts/ndesk-permission-grant.schema.json`, `contracts/ndesk-session-telemetry.schema.json`) não têm nenhum campo específico de Windows 7 ou de arquitetura Win32/C++ — permanecem válidos sem alteração de schema.

## Decisão

### 1. Plataforma alvo

- **Windows 10 (baseline: 21H2 ou superior) e Windows 11.** Windows 7, Windows 8 e Windows 8.1 ficam **fora de escopo** do NDesk a partir deste ADR.
- Nenhum requisito operacional, critério de aceite ou spike deste módulo deve mais referenciar Windows 7/8/8.1 como alvo suportado.

### 2. Agente temporário: de Win32/C++ para .NET moderno

- O agente temporário do NDesk deixa de ser um binário **Win32/C++ nativo** (`ADR-007`) e passa a ser **.NET moderno, publicado como single-file self-contained**.
- Continua **temporário por design**: sem instalar serviço Windows, sem persistência silenciosa, sem processo residente além da sessão consentida — os mesmos controles obrigatórios de `ADR-015`/`docs/22` continuam valendo, agora implementados em C#/.NET em vez de C++.
- Self-contained elimina a necessidade de o usuário atendido ter o runtime .NET pré-instalado — a restrição histórica "não exige .NET moderno" (que existia por causa do Windows 7) deixa de fazer sentido como regra de design; o que se mantém é o objetivo original por trás dela: **nenhuma instalação de pré-requisito na máquina atendida**, agora alcançado via publish self-contained em vez de runtime estático C/C++.

### 3. Captura de tela e input: DXGI + abstração para portabilidade futura

- **Windows 10/11: DXGI Desktop Duplication** como caminho de captura primário (substitui a antiga matriz por versão de `docs/22` que tratava GDI BitBlt/Win7 e DXGI/Win8 como casos separados).
- Captura e injeção de input passam a ficar **atrás de uma interface** (ex.: `IScreenCaptureProvider`/`IInputInjector` — nomes finais a definir na implementação) para permitir, no roadmap futuro, implementações equivalentes em Linux (PipeWire) e macOS (`CGDisplayStream`) sem reabrir a arquitetura do agente. Este ADR **não compromete** um cronograma de portabilidade — apenas garante que a decisão de hoje não feche essa porta.

### 4. Reavaliação buy-vs-build (gatilho do critério de reversão da ADR-015)

A remoção do requisito Windows 7 dispara explicitamente o critério de reversão da `ADR-015`. Reavaliando os candidatos de compra (`SPIKE-016`) **sem** o fator Windows 7:

- **RustDesk** continua desqualificado: licença **AGPL-3.0** sobre um cliente derivado distribuído a terceiros (incompatível com o modelo de negócio declarado) e capacidade oficial de compor um cliente sem indicador visível e sem botão de parar (`hide-tray`/`hide-stop-service`) — ambos **independentes de Windows 7**.
- **MeshCentral** continua desqualificado: consentimento configurável para silencioso (bitmask de flags = 0), modo Intel AMT estruturalmente sem consentimento por rodar abaixo do SO, e instalação padrão como **serviço Windows persistente** — os três bloqueios são **independentes de Windows 7**.
- **Apache Guacamole** continua fora de escopo pelo mesmo motivo estrutural de sempre: nunca existiu consentimento ad-hoc, é um gateway para infraestrutura já gerenciada, não assistência a usuário final desconhecido.

**Conclusão da reavaliação: "construir" permanece a decisão correta.** O que muda não é o buy-vs-build — é a **tecnologia** do lado "construir": o agente pivota de Win32/C++ para .NET moderno porque o único motivo técnico para C/C++ (compatibilidade com Windows 7 sem .NET) deixou de existir. `ADR-015` permanece confirmada nesta forma revisada; não é necessária uma ADR de buy-vs-build nova.

## Consequências positivas

- **Menor superfície de risco de memória.** Código gerenciado (.NET) em vez de C/C++ novo sem auditoria externa elimina a classe de risco mais citada em `ADR-015`/`SPIKE-016` ("código de parsing de rede novo em C/C++, sem histórico de auditoria, é a maior fonte de risco de implementação do primeiro ano").
- **Toolchain atual, sem prazo de expiração.** O risco de "Visual Studio 2022 mainstream até ~jan/2027 sem sucessor viável para Windows 7" deixa de existir — o time pode usar o toolchain .NET/VS atual sem uma janela de suporte artificial.
- **O agente .NET é o que habilita o roadmap cross-platform.** Uma base .NET (com captura/input abstraídos) é o caminho mais direto para Linux/macOS no futuro — muito mais viável do que portar C/C++ Win32 nativo, ou manter duas implementações nativas separadas.
- Reaproveitamento potencial de bibliotecas/padrões já usados no RemoteOps Desktop (C#/.NET/WPF), reduzindo a divergência de stack entre módulos.
- 100% dos contratos (`ndesk-ticket`/`ndesk-permission-grant`/`ndesk-session-telemetry`) e do desenho de produto/segurança permanecem válidos sem alteração de schema.

## Consequências negativas

- **Perda de compatibilidade com Windows 7/8/8.1.** Máquinas legadas deixam de ser atendíveis pelo NDesk até uma decisão de produto explícita reabrir esse escopo (não prevista neste ADR).
- **Tamanho de binário maior que Win32/C++ nativo.** Um publish .NET self-contained single-file (mesmo com trimming/ReadyToRun) tende a ser sensivelmente maior que um binário C++ estático equivalente — a ser medido no spike de implementação; se necessário, avaliar Native AOT para reduzir tamanho e tempo de start.
- **Reputação de antivírus/EDR precisa ser revalidada para o novo binário.** O controle "assinatura + comportamento transparente" de `ADR-015`/`docs/22` continua obrigatório, mas o perfil de detecção de um executável .NET self-contained é diferente do de um binário C++ nativo e não pode ser assumido como equivalente sem teste.
- **Abstração de captura/input introduz complexidade antes de haver compromisso de cronograma para Linux/macOS.** É uma decisão deliberada (evitar reescrever a interface de captura quando a portabilidade for de fato priorizada), mas registra-se o trade-off explicitamente: a interface deve ficar simples o bastante para não virar abstração especulativa sem uso real no curto prazo.
- Spikes já mapeados em `docs/15-pesquisa-e-spikes.md` referenciando Windows 7 (notadamente SPIKE-011 — "NDesk Windows 7 legado") ficam órfãos e precisam de decisão de descontinuação ou reformulação em atualização futura de `docs/15` — fora do escopo desta PR, registrado aqui para rastreabilidade.
- A escolha final da stack de transporte nativo (`libwebrtc` completo vs. stack C# gerenciada) permanece **em aberto**, a resolver em `SPIKE-017`/`ADR-017` — ver revisão de `ADR-005`.

## Alternativas consideradas

1. **Manter o agente Win32/C++ nativo, apenas removendo Windows 7 do requisito de captura/toolchain.** Rejeitada: a única razão para escolher C++ nativo em `ADR-007` era compatibilidade com Windows 7 sem .NET/Java/WebView2. Sem esse requisito, manter C/C++ significa carregar a complexidade e o risco de memory-safety de um binário nativo sem nenhum benefício de compatibilidade correspondente.
2. **Agente em Rust.** `ADR-005` já deixava essa porta aberta ("worker Rust apenas se necessário") como opção de memory-safety para componentes de parsing intensivo. Não descartada para o futuro — mas este pivô adota .NET moderno como decisão principal do agente por reaproveitar a stack já usada no Desktop; Rust permanece disponível como opção pontual para sub-componentes isolados, se um spike futuro justificar.
3. **Não pivotar agora — aguardar o spike dedicado de `libdatachannel`+Windows 7 antes de qualquer mudança.** Rejeitada: a decisão de produto de remover Windows 7 já foi tomada e é anterior/independente do resultado desse spike; adiar o ADR não muda a decisão de escopo, só atrasa a atualização da documentação.

## Critério de revisão futura

Revisar este ADR se:

- o suporte a Windows 7/8/8.1 for reintroduzido no roadmap do produto — reabre a pergunta de toolchain (Win32/C++ vs .NET) e potencialmente reabre `ADR-015` em sentido contrário;
- o binário .NET self-contained se provar consistentemente sinalizado por antivírus/EDR de forma não mitigável por assinatura/reputação, a ponto de inviabilizar a distribuição via link direto;
- o roadmap de Linux/macOS for formalmente priorizado — nesse caso, a interface de captura/input abstraída criada por este ADR deve ser revisada quanto à sua adequação real (não apenas teórica);
- `SPIKE-017`/`ADR-017` (escolha de stack de transporte) concluir por uma stack que não seja compatível com .NET gerenciado, exigindo reabrir a decisão de linguagem do agente.

## Referências

- `adr/ADR-007-ndesk-agente-legado-win32.md` — superada por este ADR.
- `adr/ADR-005-acesso-remoto-webrtc.md` — revisada por este ADR (stack de transporte reaberta).
- `adr/ADR-015-ndesk-buy-vs-build.md` — reavaliada; conclusão "construir" mantida.
- `docs/spikes/SPIKE-016-ndesk-buy-vs-build.md` — evidência original de licença/consentimento usada na reavaliação.
- `docs/09-acesso-remoto-ndesk.md`, `docs/22-ndesk-performance-legacy-windows.md` — atualizados por este ADR (matriz Win10/11, remoção de seções Win7).
- `contracts/ndesk-ticket.schema.json`, `contracts/ndesk-permission-grant.schema.json`, `contracts/ndesk-session-telemetry.schema.json` — sem alteração de schema.
