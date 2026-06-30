# 14 — Operação, suporte e administração

## Operação do backend

- Rodar em VM Linux com IPv4 e IPv6 públicos.
- Usar Docker Compose inicialmente.
- TLS obrigatório.
- PostgreSQL com backup diário.
- Monitoramento de CPU, RAM, disco, latência e erros.
- Alertas de falha de sync, falha de login e sessão NDesk anômala.

## Operação do desktop

- Instalador assinado.
- Canal de atualização interno.
- Configuração por tenant/workspace.
- Logs locais com rotação.
- Modo diagnóstico sem segredos.

## Administração

Telas administrativas:

- Usuários.
- Dispositivos.
- Workspaces.
- Grupos.
- Credenciais.
- Políticas.
- Auditoria.
- Convites NDesk.

## Políticas configuráveis

- Exigir MFA.
- Expiração de sessão.
- Expiração de cache local.
- Permitir Telnet.
- Permitir exportação.
- Permitir revelar senha.
- Permitir drive redirect RDP.
- Permitir clipboard RDP.
- Permitir gravação de NDesk.
- Exigir aprovação para mudanças de credencial.

## Backup e restore

- Backup PostgreSQL diário.
- Retenção mínima: 30 dias.
- Teste de restore mensal.
- Chaves de recuperação armazenadas separadamente.
- Procedimento de disaster recovery documentado.

## Runbooks iniciais

- Como revogar usuário.
- Como revogar dispositivo perdido.
- Como rotacionar credencial de grupo.
- Como restaurar backup.
- Como investigar acesso indevido.
- Como renovar certificado TLS.
- Como publicar nova versão desktop.

## Métricas

- Usuários ativos.
- Hosts cadastrados.
- Sessões por protocolo.
- Falhas de conexão por protocolo.
- Sync lag médio.
- Erros de conflito.
- Convites NDesk criados/usados/expirados.
- Uso de relay.

## Política de logs

- Logs de aplicação sem segredos.
- Audit log imutável ou append-only.
- Retenção por política interna.
- Exportação para SIEM futura.
