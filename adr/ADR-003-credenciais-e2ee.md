# ADR-003 — Credenciais, envelope encryption e DPAPI

## Status

Proposta inicial.

## Contexto

O sistema sincroniza senhas e chaves privadas. O servidor não deve ser ponto único de vazamento de plaintext.

## Decisão

Criptografar segredos com envelope encryption por workspace. Proteger chaves locais com DPAPI no Windows. Usar SQLCipher ou equivalente para o banco local.

## Consequências positivas

- Reduz impacto de vazamento do banco servidor.
- Protege cache local em notebook perdido.
- Permite rotação por workspace/credencial.

## Consequências negativas

- Recuperação de chave precisa processo formal.
- E2EE adiciona complexidade a multiusuário.
- Debug deve ser feito sem inspecionar plaintext.

## Regras

- Nunca salvar plaintext.
- Nunca logar segredo.
- Recuperação exige auditoria.
- Revelar senha, se existir, exige permissão especial.
