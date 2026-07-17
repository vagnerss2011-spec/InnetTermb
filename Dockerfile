# RemoteOps Cloud — imagem da API (deploy Debian 13, ver docs/runbook-deploy-debian.md)
#
# Multi-stage: o SDK só existe no estágio de build; a imagem final é o runtime
# ASP.NET, sem compilador e sem código-fonte.
#
# ATENÇÃO: builda o PROJETO, não o RemoteOps.sln. A solution tem RemoteOps.Desktop
# (WPF/net10.0-windows), que não restaura em Linux — `dotnet restore` da sln aqui
# quebra. O ProjectReference puxa RemoteOps.Contracts sozinho.

# ── Build ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Directory.Build.props vale para todos os projetos (Nullable, TreatWarningsAsErrors):
# sem ele o build de dentro do container não é o mesmo do CI.
COPY Directory.Build.props ./
COPY src/RemoteOps.Cloud/RemoteOps.Cloud.csproj src/RemoteOps.Cloud/
COPY src/RemoteOps.Contracts/RemoteOps.Contracts.csproj src/RemoteOps.Contracts/

# Restore antes de copiar o código: muda o .csproj → refaz; muda só o código → cache.
RUN dotnet restore src/RemoteOps.Cloud/RemoteOps.Cloud.csproj

COPY src/RemoteOps.Cloud/ src/RemoteOps.Cloud/
COPY src/RemoteOps.Contracts/ src/RemoteOps.Contracts/

RUN dotnet publish src/RemoteOps.Cloud/RemoteOps.Cloud.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ── Runtime ──────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl entra só para o HEALTHCHECK (a imagem aspnet não traz cliente HTTP de linha
# de comando). Sem healthcheck, o Caddy manda tráfego para uma API que ainda está
# aplicando migration e o operador vê 502 sem explicação.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

# Porta interna. Quem termina TLS é o Caddy (ver docker-compose.yml); esta porta
# NÃO é publicada no host.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Usuário não-root que já vem na imagem (UID 1654). Produto de credenciais não roda
# como root nem por conveniência de deploy.
USER $APP_UID

HEALTHCHECK --interval=15s --timeout=3s --start-period=40s --retries=5 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "RemoteOps.Cloud.dll"]
