using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RemoteOps.NDesk.Broker.Audit;
using RemoteOps.NDesk.Broker.Consent;
using RemoteOps.NDesk.Broker.Data;
using RemoteOps.NDesk.Broker.Errors;
using RemoteOps.NDesk.Broker.Signaling;
using RemoteOps.NDesk.Broker.Telemetry;
using RemoteOps.NDesk.Broker.Tickets;

var builder = WebApplication.CreateBuilder(args);

// ── Banco de dados ──────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionString 'Default' não configurada. Use variável de ambiente ConnectionStrings__Default.");

builder.Services.AddDbContext<NDeskDbContext>(opt => opt.UseNpgsql(connectionString));

// ── Autenticação JWT (mesmo emissor do RemoteOps.Cloud — operador já autenticado) ──────────
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

// ── SignalR ──────────────────────────────────────────────────────────────────
builder.Services.AddSignalR();

// ── Relógio testável (TicketService/PermissionGrantService dependem de TimeProvider) ────────
builder.Services.AddSingleton(TimeProvider.System);

// ── Serviços de domínio ──────────────────────────────────────────────────────
builder.Services.AddScoped<NDeskAuditService>();
builder.Services.AddScoped<NDeskTicketService>();
builder.Services.AddScoped<NDeskPermissionGrantService>();
builder.Services.AddScoped<NDeskTelemetryService>();

// ── Tratamento de erros ──────────────────────────────────────────────────────
builder.Services.AddExceptionHandler<NDeskExceptionHandler>();
builder.Services.AddProblemDetails();

// ── Observabilidade ──────────────────────────────────────────────────────────
builder.Services.AddHttpLogging(opt =>
{
    opt.CombineLogs = true;
    // Nunca logar Authorization header nem query string (que pode carregar access_token do hub)
    opt.RequestHeaders.Remove("Authorization");
    opt.ResponseHeaders.Remove("Set-Cookie");
    opt.LoggingFields &= ~Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestQuery;
});

var app = builder.Build();

// ── Inicialização do schema ───────────────────────────────────────────────────
// Sem isto, o broker não sobe contra um banco novo (as tabelas não existem e a
// primeira escrita falha). EnsureCreated cria o schema a partir do modelo EF — suficiente
// para MVP/dev e para os ambientes atuais. Débito registrado (ADR-018 §débitos): migrations
// versionadas (`dotnet ef migrations`) devem substituir EnsureCreated antes de evoluir schema
// em produção ou escalar horizontalmente, já que EnsureCreated não versiona alterações.
// Desligável por NDESK_DB_SKIP_INIT=true (ex.: quando um migrador externo cuida do schema).
if (!string.Equals(
        Environment.GetEnvironmentVariable("NDESK_DB_SKIP_INIT"), "true", StringComparison.OrdinalIgnoreCase))
{
    using IServiceScope scope = app.Services.CreateScope();
    NDeskDbContext db = scope.ServiceProvider.GetRequiredService<NDeskDbContext>();
    db.Database.EnsureCreated();
}

// ── Middlewares ──────────────────────────────────────────────────────────────
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.MapNDeskTicketEndpoints();
app.MapNDeskConsentEndpoints();
app.MapNDeskTelemetryEndpoints();

// ── SignalR Hub ───────────────────────────────────────────────────────────────
app.MapHub<NDeskSignalingHub>("/hubs/ndesk");

app.Run();

// Partial class para tornar Program acessível em testes de integração
public partial class Program { }
