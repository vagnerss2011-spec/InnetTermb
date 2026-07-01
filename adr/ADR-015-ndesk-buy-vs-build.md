# ADR-015 — NDesk: confirmação de "construir" sobre comprar solução self-hosted

## Status

Proposta (spike concluído, aguardando revisão do orquestrador). Confirma `ADR-005` e `ADR-007`, com revisões.

## Contexto

O NDesk já tinha uma direção arquitetural provisória: `ADR-005-acesso-remoto-webrtc.md` (WebRTC + broker próprio) e `ADR-007-ndesk-agente-legado-win32.md` (agente Win32/C++ nativo para Windows legado), com contratos já desenhados (`contracts/ndesk-ticket.schema.json`, `contracts/ndesk-permission-grant.schema.json`, `contracts/ndesk-session-telemetry.schema.json`). O SPIKE-016 (`docs/spikes/SPIKE-016-ndesk-buy-vs-build.md`) avaliou, com pesquisa de fontes primárias e verificação adversarial, se essa direção deveria ser mantida ou se uma solução self-hosted open-source (RustDesk, MeshCentral, Apache Guacamole) deveria ser adotada/adaptada em vez de construir.

O NDesk é um módulo sensível por natureza (assistência remota consentida, possível elevação administrativa, possível distribuição futura de agente assinado a terceiros) e está sujeito aos princípios obrigatórios do `CLAUDE.md`: sem segredo em texto puro, sem acesso remoto oculto/persistência silenciosa/bypass de consentimento/evasão de antivírus/coleta de credenciais, consentimento visível + revogação imediata + auditoria obrigatórios.

## Opções consideradas

1. **RustDesk self-hosted** (`hbbs`/`hbbr` próprio + cliente `rustdesk`).
2. **MeshCentral** (servidor + `MeshAgent`).
3. **Apache Guacamole** (gateway clientless RDP/VNC/SSH via `guacd`).
4. **Construir do zero** — WebRTC + agente Win32/C++ nativo + broker/relay (STUN/TURN) próprios, o caminho de `ADR-005`/`ADR-007`.

Detalhamento completo, matriz de decisão candidato×critério com fontes citadas, e pareceres independentes do `security-agent` e `ndesk-agent`: ver `docs/spikes/SPIKE-016-ndesk-buy-vs-build.md`.

## Decisão

**Confirmar o caminho "construir" — manter `ADR-005` e `ADR-007` como direção arquitetural do NDesk — e não adotar RustDesk, MeshCentral ou Apache Guacamole como base do módulo.**

Motivos decisivos (evidência completa e fontes no relatório do spike):

1. **Licença.** RustDesk (cliente e servidor) é **AGPL-3.0**, confirmado no arquivo LICENSE/LICENCE dos repositórios oficiais. Distribuir um cliente derivado/modificado a terceiros — o modelo de negócio declarado para o NDesk ("agente assinado para terceiros") — obrigaria a abrir o código desse agente sob AGPL-3.0, o que é incompatível com um produto que pode ser comercializado de forma fechada. MeshCentral e Guacamole são Apache-2.0 (sem esse bloqueio), mas são desqualificados pelos motivos 2 e 3 abaixo.
2. **Requisitos inegociáveis do `CLAUDE.md` não são garantíveis sem depender de configuração de terceiro.** RustDesk permite oficialmente compor um cliente sem indicador visível e sem botão de parar (`hide-tray`, `hide-stop-service`, aviso de senha preset suprimido por padrão) e permite habilitar acesso não supervisionado remotamente sem interação local. MeshCentral permite configurar o consentimento para completamente silencioso (bitmask de flags = 0, confirmado no código-fonte) e tem um modo (Intel AMT) estruturalmente incapaz de exibir consentimento por rodar abaixo do SO. Guacamole nunca teve consentimento ad-hoc — não é um modo a desligar, é ausência estrutural, porque o produto resolve outro problema (gateway para infraestrutura já gerenciada, não assistência a usuário final desconhecido).
3. **Windows 7 SP1.** Requisito operacional explícito de `docs/22-ndesk-performance-legacy-windows.md`. RustDesk abandonou Windows 7 desde ~2023 (confirmado por mantenedores). MeshCentral tem suporte "best-effort" frágil, com regressões abertas sem resposta. Guacamole não se aplica (sem agente). Só o caminho "construir" trata Win7 como requisito de design de primeira classe — embora este spike tenha descoberto um risco novo de toolchain que também o afeta (ver Consequências negativas).
4. **Preservação de investimento.** O código de "construir" hoje é apenas stub fora da solution (custo de descarte ~zero em qualquer cenário), mas os contratos (`ndesk-ticket`/`ndesk-permission-grant`/`ndesk-session-telemetry`, já com POCOs em `src/RemoteOps.Contracts/NDesk/*.cs`) e o desenho de produto/segurança (`docs/09`, `docs/22`, `docs/05`) só sobrevivem 100% no caminho "construir" — todo candidato de compra descarta parcial ou totalmente essa superfície.
5. **Convergência de pareceres independentes.** `security-agent` (ótica de risco: ranking do mais ao menos arriscado — MeshCentral > RustDesk > Guacamole > Construir) e `ndesk-agent` (ótica de engenharia/fit: ranking do melhor ao pior encaixe — Construir > MeshCentral > RustDesk > Guacamole) chegaram, de forma independente entre si e da pesquisa web, à mesma conclusão: nenhum candidato de compra é recomendável para o NDesk.

### Revisões incorporadas a `ADR-005`/`ADR-007` por este ADR

- **Preferir bibliotecas maduras específicas em vez de reinventar ICE/DTLS/SCTP do zero, e evitar embarcar o `libwebrtc` completo do Chromium.** `ADR-005` já previa "reuso de tecnologia real-time madura" sem especificar; este spike encontrou que embarcar o `libwebrtc` completo não é viável para Windows 7 (Chromium abandonou Win7/8/8.1 desde o Chrome 109/jan-2023; qualquer branch atual não builda/roda em Win7) e é ~30x maior que a alternativa leve. **`libdatachannel`** (MPL-2.0, C++17, ativo) é a biblioteca recomendada para o data channel/mídia nativa; **`coturn`** (BSD-3) ou **`eturnal`** (Apache-2.0) são os candidatos maduros para TURN self-hosted — `eturnal` avaliado como mais consistentemente ativo em comparativo independente, dado que `coturn` teve um período de 18 meses sem release.
- **Novo risco crítico a rastrear em `ADR-007`: toolchain de build para Windows 7.** O Visual Studio 2026 atual removeu Windows 7 como plataforma de deployment suportada, sem workaround documentado; o time depende do Visual Studio 2022, cujo suporte mainstream termina em ~janeiro/2027. Isso é independente da escolha de biblioteca WebRTC e ameaça a viabilidade de longo prazo do suporte a Win7 se não for endereçado explicitamente (fixar imagem de build VS2022, e/ou definir data de revisão do compromisso de suporte a Win7).
- **Suporte a Windows 7 do `libdatachannel` não está confirmado nem negado em nenhuma fonte oficial** — recomenda-se um spike técnico dedicado (compilar e conectar de fato em Windows 7 SP1 de laboratório) antes de comprometer a arquitetura de transporte legado, complementando o SPIKE-011 já previsto em `docs/15-pesquisa-e-spikes.md`.

## Consequências positivas

- Consentimento, ausência de persistência silenciosa, banner visível e auditoria continuam sendo propriedades de design verificáveis pelo `security-agent`, não configuração a manter travada sobre produto de terceiro.
- Nenhuma herança de obrigação de licença AGPL nem dependência de licenciamento comercial de terceiro para o cenário de agente distribuído a terceiros.
- 100% do investimento já feito em contratos e documentação de produto/segurança permanece válido.
- Superfície de ataque inicial menor que qualquer dos candidatos de compra (sem console web genérico, sem AMT, sem gerenciador de arquivo de propósito geral).

## Consequências negativas

- Maior esforço de implementação bruto que adotar uma solução pronta — já orçado no roadmap (`docs/24-orquestracao-multiagente-paralela.md`) e distribuído nos spikes técnicos de `docs/15-pesquisa-e-spikes.md` (SPIKE-010 a SPIKE-014).
- Código novo de captura/codec/parsing de protocolo em C/C++, sem histórico de auditoria externa, rodando por requisito em Windows 7 desatualizado, é a maior fonte de risco de implementação do primeiro ano — mitigar com fuzzing/ASan em CI, sandboxing do processo que faz parsing de mensagens de rede, e considerar Rust para os componentes novos de parsing (`ADR-005` já deixava essa porta aberta: "worker Rust apenas se necessário").
- Novo risco de toolchain (Visual Studio 2026 sem suporte a deployment Win7; VS2022 mainstream até ~jan/2027) precisa de decisão explícita de janela de suporte a Windows 7 e de revisão periódica — não é mais só uma questão de biblioteca/driver de captura.
- Suporte a Windows 7 do `libdatachannel` ainda não confirmado — risco técnico em aberto até o spike dedicado (ver "Próximos passos" no relatório do spike).
- Sem o benefício de correções de segurança de uma comunidade externa ativa — o ônus de descoberta e correção de vulnerabilidade é inteiramente interno.

## Controles obrigatórios (gate de produção, independente de qualquer sub-decisão futura)

Checklist completo produzido pelo `security-agent` durante este spike (21 controles) está registrado em `docs/spikes/SPIKE-016-ndesk-buy-vs-build.md`. Destaques que se tornam critério de aceite de qualquer PR de implementação do NDesk:

- Nenhuma sessão inicia sem evento de consentimento explícito populando `ndesk-permission-grant.schema.json`.
- Indicador visível permanente sempre que o agente está apto a aceitar conexão e durante sessão ativa; nenhum processo/UI oculto.
- Botão "encerrar sessão" funcional do lado atendido, efetivo imediatamente.
- Modo administrador segue o fluxo de 8 passos de `docs/22-ndesk-performance-legacy-windows.md` (solicitação → explicação → aceite → UAC real → elevação/helper temporário → badge → encerramento a qualquer momento → remoção do helper), sem captura/mascaramento da Secure Desktop.
- Sem Intel AMT/KVM out-of-band (ou equivalente abaixo do SO) no fluxo de assistência ad-hoc consentida.
- Nenhuma senha/chave/token em texto puro; nenhum log com conteúdo de tela, senha ou conteúdo de arquivo.
- Revisão de segurança dedicada (`security-agent`) antes de habilitar qualquer recurso sensível em produção, atrás de feature flag default-off — mesmo padrão já usado para `rdp.enabled`.

## Critério de reversão futura

Revisar este ADR se:

- o requisito de suporte a Windows 7 SP1 for removido do roadmap do produto (enfraquece o argumento decisivo contra MeshCentral/RustDesk nesse critério, embora os critérios de licença/consentimento continuem valendo para RustDesk);
- o RustDesk (ou um candidato equivalente) relicenciar sob termos permissivos, ou a empresa negociar uma licença comercial compatível com distribuição fechada a terceiros;
- o spike técnico dedicado mostrar que `libdatachannel` (ou a alternativa Amazon Kinesis Video Streams WebRTC SDK for C) não é viável em Windows 7 SP1, sem alternativa leve equivalente;
- o Visual Studio 2022 chegar ao fim do suporte mainstream (~jan/2027) sem um caminho de sucessor viável para compilar/assinar o agente Win32 visando Windows 7;
- o custo de manutenção do código C/C++ próprio (correção de vulnerabilidade, captura, codec) se provar excessivo frente ao roadmap, conforme o critério de revisão já previsto em `ADR-007`.

## Referências

- `docs/spikes/SPIKE-016-ndesk-buy-vs-build.md` — relatório completo, matriz de decisão, fontes primárias citadas por candidato.
- `adr/ADR-005-acesso-remoto-webrtc.md` — confirmado, com as revisões descritas acima.
- `adr/ADR-007-ndesk-agente-legado-win32.md` — confirmado, com o novo risco de toolchain Win7/VS2026 incorporado ao seu critério de revisão futura.
- `docs/09-acesso-remoto-ndesk.md`, `docs/22-ndesk-performance-legacy-windows.md`, `docs/05-seguranca-credenciais-threat-model.md`.
- `contracts/ndesk-ticket.schema.json`, `contracts/ndesk-permission-grant.schema.json`, `contracts/ndesk-session-telemetry.schema.json`.
