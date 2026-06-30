# 20 — Migração, importação e exportação

## Objetivo

Facilitar adoção importando cadastros existentes sem comprometer segurança.

## Fontes possíveis

- CSV interno.
- Planilhas controladas.
- Exportações de ferramentas atuais, se legalmente e tecnicamente permitido.
- Inventário NetBox/GLPI/Zabbix/LibreNMS futuramente.

## Importação CSV MVP

Campos sugeridos:

```csv
group,name,vendor,fqdn,ipv4,ipv6,protocol,port,username,credentialGroup,tags
Core,router01,cisco,router01.local,10.0.0.1,2001:db8::1,ssh,22,admin,Senha Core,"core,ipv6"
```

## Regras

- Senhas em CSV devem ser evitadas.
- Se importar senha, exigir arquivo local, criptografar imediatamente e apagar staging.
- Mostrar preview antes de aplicar.
- Validar duplicidades.
- Registrar auditoria.
- Permitir dry-run.

## Exportação

Exportar inventário sem segredos por padrão.

Exportação com segredos:

- Desabilitada por padrão.
- Exige permissão especial.
- Exige justificativa.
- Deve gerar arquivo criptografado.
- Deve registrar auditoria forte.

## Deduplicação

Chave sugerida:

- `workspace + normalizedName`, ou
- `workspace + fqdn`, ou
- `workspace + ip`, conforme política.

## Pós-MVP

- Import de NetBox.
- Import de Zabbix/LibreNMS.
- Descoberta controlada por subnets autorizadas.
- Export para auditoria.

## Importação de MikroTik/WinBox

Se a empresa tiver listas existentes de MikroTik, preferir importar por CSV/planilha controlada. Importar diretamente arquivos internos do WinBox deve ser evitado no MVP, porque o RemoteOps deve ser a fonte de verdade e não deve depender de formato privado.

Campos adicionais para MikroTik:

```csv
group,name,vendor,ipv4,ipv6,winboxPort,sshPort,username,credentialGroup,preferIpv6,winboxWorkspace,tags
Core,mk-borda-01,mikrotik,198.51.100.10,2001:db8::10,8291,22,admin,Senha MikroTik,true,<own>,"routeros,borda"
```

Regras:

- Não importar senha de `winbox.cfg`.
- Não tratar lista gerenciada do WinBox como cofre.
- Validar IPv6 e porta WinBox.
- Marcar hosts como `vendor=mikrotik` para habilitar botão `Abrir WinBox`.

## Importação para NDesk

NDesk não importa máquinas de terceiros no MVP. O fluxo é por convite temporário. Para modo instalado futuro em máquinas internas, criar importação separada com aprovação administrativa, política visível e auditoria.
