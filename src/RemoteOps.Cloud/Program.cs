using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RemoteOps.Cloud.Audit;
using RemoteOps.Cloud.Auth;
using RemoteOps.Cloud.Configuration;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Email;
using RemoteOps.Cloud.Errors;
using RemoteOps.Cloud.Hubs;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Secrets;
using RemoteOps.Cloud.Sync;
using RemoteOps.Cloud.Teams;

var builder = WebApplication.CreateBuilder(args);

// ── Banco de dados ──────────────────────────────────────────────────────────
var connectionString = DeploymentConfig.ResolveConnectionString(builder.Configuration);

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(connectionString));

// ── Autenticação JWT ─────────────────────────────────────────────────────────
var jwtKey = DeploymentConfig.ResolveJwtSigningKey(builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(jwtKey),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // Permite JWT via query string para SignalR (WebSocket não suporta header Authorization)
        opt.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var access = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(access) && path.StartsWithSegments("/hubs"))
                    ctx.Token = access;
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// ── Proxy confiável (X-Forwarded-For) ────────────────────────────────────────
// Atrás do Caddy, sem isto o RemoteIpAddress seria sempre o IP do proxy: o rate limit
// por IP viraria um balde global e os logs registrariam só o Caddy. Configura em quais
// faixas confiar (default = bridges do Docker; override por TRUSTED_PROXY_CIDR). O
// middleware é aplicado ANTES do rate-limiter no pipeline (ver app.UseForwardedHeaders).
ForwardedHeadersSetup.Configure(builder.Services, builder.Configuration);

// ── Rate limit do /auth ──────────────────────────────────────────────────────
// Freia força-bruta de AuthHash e enumeração via /auth/kdf. Particionado por IP:
// o /auth/kdf é anônimo, então não há identidade melhor antes do login.
builder.Services.AddRateLimiter(opt =>
{
    opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opt.AddPolicy(AuthEndpoints.RateLimitPolicy, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ── SignalR ──────────────────────────────────────────────────────────────────
builder.Services.AddSignalR();

// ── Serviços de domínio ──────────────────────────────────────────────────────
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<MfaService>();
builder.Services.AddScoped<PasswordResetService>();
// Enviador de email (recuperação de senha, Fase 4): SMTP se configurado, senão log (dev/CI).
builder.Services.AddEmailSender(builder.Configuration);
// Protetor do segredo TOTP em repouso: sem estado próprio, derivado da config → singleton.
builder.Services.AddSingleton<MfaSecretProtector>();
builder.Services.AddScoped<PermissionEvaluator>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<SecretsService>();
builder.Services.AddScoped<AuditService>();
// Times compartilhados (Fatia 1): convite por código fora-de-banda + membros.
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<TeamService>();

// ── Tratamento de erros ──────────────────────────────────────────────────────
builder.Services.AddExceptionHandler<CloudExceptionHandler>();
builder.Services.AddProblemDetails();

// ── Observabilidade ──────────────────────────────────────────────────────────
builder.Services.AddHttpLogging(opt =>
{
    opt.CombineLogs = true;
    // Nunca logar Authorization header
    opt.RequestHeaders.Remove("Authorization");
    opt.ResponseHeaders.Remove("Set-Cookie");
});

var app = builder.Build();

// ── Schema do banco ──────────────────────────────────────────────────────────
// Antes de atender request: o container tem que subir com o schema certo ou não
// subir. Falhar aqui é melhor que servir 500 no primeiro /auth/register.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseBootstrapper");
    DatabaseBootstrapper.MigrateIfRelational(db, logger);
}

// Deixa explícito no log de startup se os emails de recuperação vão SAIR (SMTP) ou só ir pro log.
app.Logger.LogInformation(
    "Recuperação de senha por email: enviador = {Sender}",
    EmailServiceSetup.SmtpConfigured(app.Configuration) ? "SMTP" : "Log (Smtp:Host não configurado)");

// ── Middlewares ──────────────────────────────────────────────────────────────
// PRIMEIRO de tudo: reescreve o RemoteIpAddress a partir do X-Forwarded-For do proxy
// confiável. Tem que rodar ANTES do rate-limiter (que particiona por IP) e antes de
// qualquer log de IP — senão todos veriam o IP do Caddy, não o do cliente.
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.MapAuthEndpoints();
app.MapSyncEndpoints();
app.MapSecretsEndpoints();
app.MapTeamEndpoints();

// ── SignalR Hub ───────────────────────────────────────────────────────────────
app.MapHub<SyncHub>("/hubs/sync");

app.Run();

// Partial class para tornar Program acessível em testes de integração
public partial class Program { }
