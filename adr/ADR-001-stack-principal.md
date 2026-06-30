# ADR-001 — Stack principal do projeto

## Status

Proposta inicial.

## Contexto

O sistema precisa rodar no Windows, abrir RDP, integrar terminal SSH/Telnet, armazenar credenciais e sincronizar em nuvem. A equipe pretende usar agentes de coding em paralelo, então produtividade, testabilidade e separação de módulos são prioritárias.

## Decisão

Usar C#/.NET 10 + WPF para o desktop e ASP.NET Core para o backend. Usar Rust apenas como worker nativo opcional para o módulo NDesk, se necessário.

## Consequências positivas

- Uma linguagem principal para desktop, backend, testes e contratos.
- Boa integração Windows.
- Interop com RDP ActiveX via WPF/WinForms.
- WebView2/xterm.js viável para terminal moderno.
- Facilidade para agentes dividirem módulos.

## Consequências negativas

- Cliente inicial fica Windows-only.
- WPF é menos moderno que WinUI 3 visualmente.
- Interop ActiveX exige cuidado.

## Alternativas consideradas

- Go: bom para backend, fraco para UI Windows/RDP embedded.
- C++: alto controle, maior custo e risco.
- Rust/Tauri: bom para performance, mas RDP/ActiveX e UI empresarial podem atrasar MVP.
- WinUI 3: moderno, mas ActiveX é mais difícil.
