# 13 — Plano de testes e QA

## Camadas de teste

### Unitários

- Serviços de domínio.
- Resolução IPv6/IPv4.
- Merge/conflict resolution.
- RBAC.
- Sanitização de logs.
- Serialização de contratos.

### Integração

- Backend + PostgreSQL.
- Sync push/pull.
- SignalR notifications.
- Vault local + DB.
- SSH contra container/lab.
- RouterOS CHR/lab quando disponível.

### E2E Desktop

- Criar grupo/host/credencial.
- Abrir SSH.
- Abrir RDP.
- Sincronizar dois clientes.
- Resolver conflito.

### Segurança

- Verificar ausência de plaintext no DB local.
- Verificar ausência de segredos em logs.
- Testar revogação de usuário/dispositivo.
- Testar host key/cert alterado.
- Testar permissões negadas.

### NDesk

- Consentimento obrigatório.
- Encerrar sessão pelo cliente.
- Token expirado não conecta.
- Controle não funciona sem permissão.
- Auditoria completa.
- Teste em redes diferentes.

## Ambientes de teste

- Windows 10.
- Windows 11.
- Windows Server com RDP/NLA.
- Linux OpenSSH.
- MikroTik RouterOS/CHR.
- Simulador Telnet legado.
- Duas redes/NATs para NDesk.

## Testes de performance

- 50 hosts na lista.
- 5.000 hosts na lista.
- 10 abas SSH simultâneas.
- 20 abas SSH simultâneas.
- Sync de 10.000 mudanças.
- NDesk com diferentes latências.

## Testes de UX

- Busca em menos de 200 ms para inventário local.
- Abrir sessão com até dois cliques.
- Indicação clara de credencial herdada.
- Feedback claro quando offline.
- Aviso claro para Telnet.

## Definition of Done por módulo

- Testes unitários relevantes.
- Testes de integração quando há IO/rede/banco.
- Logs sanitizados.
- Auditoria em ação sensível.
- Documentação atualizada.
- Critérios de aceite do documento do módulo atendidos.

## Dados de teste

Nunca usar credenciais reais. Usar fixtures sintéticas e secrets gerados para teste.

## Automação inicial

- GitHub Actions para unit/integration.
- Testes RDP/NDesk podem começar como manuais documentados e evoluir para laboratório automatizado.
