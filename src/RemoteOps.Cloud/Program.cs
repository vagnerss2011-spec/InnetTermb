using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RemoteOps.Cloud.Audit;
using RemoteOps.Cloud.Auth;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Errors;
using RemoteOps.Cloud.Hubs;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Sync;

var builder = WebApplication.CreateBuilder(args);

// ── Banco de dados ──────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionString 'Default' não configurada. Use variável de ambiente ConnectionStrings__Default.");

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(connectionString));

// ── Autenticação JWT ─────────────────────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException(
        "Jwt:SigningKey não configurada. Use variável de ambiente Jwt__SigningKey.");

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
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
builder.Services.AddScoped<PermissionEvaluator>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<AuditService>();

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

// ── Middlewares ──────────────────────────────────────────────────────────────
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.MapAuthEndpoints();
app.MapSyncEndpoints();

// ── SignalR Hub ───────────────────────────────────────────────────────────────
app.MapHub<SyncHub>("/hubs/sync");

app.Run();

// Partial class para tornar Program acessível em testes de integração
public partial class Program { }
