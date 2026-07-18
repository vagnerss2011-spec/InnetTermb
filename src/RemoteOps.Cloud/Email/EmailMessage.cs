namespace RemoteOps.Cloud.Email;

/// <summary>
/// Mensagem de email transacional (texto puro). O único email da Fase 4 é o de recuperação de
/// senha, que carrega um token de ACESSO de uso único — nunca material do cofre (a regra de ouro
/// do E2EE vale aqui também).
/// </summary>
public sealed record EmailMessage(string ToEmail, string Subject, string TextBody);
