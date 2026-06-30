# 21 — MikroTik WinBox Runner

## Objetivo

Permitir que um host cadastrado como MikroTik seja aberto diretamente no WinBox oficial, mantendo no RemoteOps a gestão de grupos, permissões, credenciais, favoritos, auditoria e sincronização em nuvem.

A ideia é simples: o produto **não reimplementa o protocolo WinBox no MVP**. Ele valida/empacota o `winbox.exe`, monta os argumentos corretos e abre o WinBox com IP, porta, usuário e, quando a política permitir, senha.

## Decisão funcional

Para hosts MikroTik, o botão principal deve oferecer:

- `Abrir WinBox`;
- `Abrir SSH`;
- `Abrir API/REST`, quando recurso futuro estiver implementado;
- `Copiar IP/IPv6`;
- `Ver auditoria`.

O cadastro MikroTik deve suportar:

- nome do host;
- cliente/tenant;
- grupo;
- IPv4;
- IPv6;
- porta WinBox, padrão `8291`;
- usuário;
- referência de credencial;
- workspace/sessão WinBox opcional;
- RoMON opcional;
- tags e observações;
- política de senha em argumento de processo.

## Layout sugerido de pasta

```text
RemoteOps/
  tools/
    winbox/
      winbox.exe
      manifest.json
      checksums.sha256
  src/
    RemoteOps.MikroTik/
      WinBoxRunner.cs
      WinBoxArgumentBuilder.cs
      WinBoxPolicy.cs
      WinBoxAudit.cs
```

## Manifesto do WinBox

O manifesto registra a versão aprovada do executável:

```json
{
  "tool": "winbox",
  "version": "4.x-approved",
  "vendor": "MikroTik",
  "file": "winbox.exe",
  "sha256": "...",
  "approvedAt": "2026-06-29T00:00:00Z",
  "approvedBy": "security-lead",
  "notes": "Versão validada em laboratório interno"
}
```

## Montagem de destino

### IPv4

```text
192.0.2.10
192.0.2.10:8292
```

### IPv6

WinBox deve receber IPv6 entre colchetes quando usado como destino:

```text
[2001:db8::10]
[2001:db8::10]:8292
[fe80::abcd%12]:8291
```

Regra: quando `preferIpv6 = true` e o host tiver IPv6 válido, o runner monta destino IPv6. Se falhar, registra erro e permite retry manual em IPv4.

## Argumentos

Formato conceitual:

```text
winbox.exe <connect-to> <login> <password> <workspace>
```

Exemplo sem senha:

```text
winbox.exe 10.5.101.1 admin "" "<own>"
```

Exemplo IPv6 com porta customizada:

```text
winbox.exe "[2001:db8::10]:8292" admin "" "<own>"
```

Exemplo RoMON conceitual:

```text
winbox.exe --romon <romon-agent> <connect-to> <login> <password> <workspace>
```

## Política de senha

Passar senha por linha de comando facilita operação, mas tem risco: argumentos de processo podem ser expostos por ferramentas de diagnóstico, EDR, logs indevidos ou dumps. Portanto o sistema deve ter três modos:

### Modo A — seguro padrão

- Passa host/porta/usuário.
- Não passa senha.
- Operador digita a senha no WinBox.
- Recomendado para ambientes de maior segurança.

### Modo B — automação permitida

- Passa host/porta/usuário/senha.
- Só permitido por política do tenant/grupo.
- Não registrar argumentos completos em logs.
- Exibir aviso administrativo na configuração.
- Registrar auditoria: `winbox_opened_with_password_argument = true`, sem a senha.

### Modo C — lista gerenciada do próprio WinBox

- Só usar se a empresa aceitar o risco.
- Não tratar o WinBox como cofre primário.
- Evitar depender de `winbox.cfg` como fonte de verdade.
- Preferir master password no WinBox quando a operação exigir lista própria do WinBox.

## Runner seguro

Regras do `WinBoxRunner`:

- usar `ProcessStartInfo.ArgumentList` ou equivalente para evitar erro de aspas;
- `UseShellExecute = false`;
- não interpolar string de comando com senha;
- não logar linha de comando completa;
- validar existência do executável;
- validar hash SHA-256 quando o executável for empacotado;
- validar permissão do usuário para abrir o host;
- validar permissão para usar senha automática;
- auditar tentativa, sucesso, falha e fallback IPv4/IPv6;
- bloquear execução se o executável não for aprovado pela política.

## Auditoria

Eventos mínimos:

- `winbox_tool_validated`;
- `winbox_open_requested`;
- `winbox_open_started`;
- `winbox_open_failed`;
- `winbox_password_argument_used`;
- `winbox_ipv6_target_used`;
- `winbox_ipv4_fallback_used`;
- `winbox_romon_used`.

Nenhum evento deve conter senha, token ou chave privada.

## UI

Na tela do host MikroTik:

- botão primário: `Abrir WinBox`;
- menu secundário: `SSH`, `Copiar endereço`, `Editar credencial`, `Ver auditoria`;
- badge: `IPv6 preferencial`, quando aplicável;
- alerta visual se o grupo permite senha via argumento;
- seletor de workspace WinBox, se habilitado.

## Integração com sync

O sync armazena o cadastro e a referência da credencial, não o arquivo WinBox. O executável WinBox é distribuído pelo instalador ou validado localmente por administrador.

## Critérios de aceite MVP

- Cadastrar host MikroTik com IPv4, IPv6, porta, usuário e credencial.
- Abrir WinBox externo com host e usuário.
- Abrir WinBox externo com senha somente quando política permitir.
- Abrir IPv6 com colchetes e porta customizada.
- Validar hash do `winbox.exe` antes de executar, quando empacotado.
- Não registrar senha em log, evento, exception ou crash report.
- Gerar evento de auditoria para toda abertura.
- Permitir desabilitar WinBox externo por tenant/grupo.

## Tarefas para agentes

### MikroTik Agent

- Implementar modelos `MikroTikHostProfile` e `WinBoxLaunchRequest`.
- Implementar `WinBoxArgumentBuilder` com testes para IPv4, IPv6, porta e RoMON.
- Implementar `WinBoxRunner` com validação de política.

### Security Agent

- Revisar risco de senha em argumento.
- Definir política padrão por tenant/grupo.
- Verificar logs e crash reports.

### Desktop Shell Agent

- Criar botão `Abrir WinBox` no perfil MikroTik.
- Exibir estado do executável validado.
- Exibir alerta quando senha automática estiver permitida.

### QA Agent

- Criar matriz de testes com IPv4, IPv6 global, IPv6 link-local, porta customizada, senha vazia, senha com espaço e RoMON.
