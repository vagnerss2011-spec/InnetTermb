var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// TODO: Adicionar auth, RBAC, SignalR, sync endpoints e NDesk broker
//       na frente feature/cloud-backend.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
