# ---- Build -------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the manifests first so a dependency-only change reuses the restore layer.
# Optional: extra root CAs for organisations that terminate and re-sign outbound TLS. The directory
# is empty by default, so this is a no-op on a normal build. See build-ca/README.md.
COPY build-ca/ /usr/local/share/ca-certificates/
RUN update-ca-certificates >/dev/null 2>&1 || true

# .editorconfig matters to the build, not just to editors: it excludes EF's generated migrations from
# analyzer enforcement, and the solution treats warnings as errors. Omitting it makes the container
# build fail where the host build succeeds.
COPY Directory.Build.props Directory.Packages.props IelBexio.slnx .editorconfig ./
COPY src/IelBexio.Domain/*.csproj                src/IelBexio.Domain/
COPY src/IelBexio.Application/*.csproj           src/IelBexio.Application/
COPY src/IelBexio.Infrastructure/*.csproj        src/IelBexio.Infrastructure/
COPY src/IelBexio.Connectors.Bexio/*.csproj      src/IelBexio.Connectors.Bexio/
COPY src/IelBexio.Connectors.Shopify/*.csproj    src/IelBexio.Connectors.Shopify/
COPY src/IelBexio.Connectors.Amazon/*.csproj     src/IelBexio.Connectors.Amazon/
COPY src/IelBexio.Ai/*.csproj                    src/IelBexio.Ai/
COPY src/IelBexio.Web/*.csproj                   src/IelBexio.Web/

RUN dotnet restore src/IelBexio.Web/IelBexio.Web.csproj

COPY src/ src/
RUN dotnet publish src/IelBexio.Web/IelBexio.Web.csproj -c Release -o /app/publish --no-restore

# ---- Runtime -----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run as a non-root user. The aspnet image ships an "app" user (uid 1654) for exactly this.
USER app

COPY --from=build --chown=app:app /app/publish ./
COPY --chown=app:app fixtures/ ./fixtures/

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_TieredPMStubs=0 \
    DOTNET_gcServer=1
EXPOSE 8080

# No image-level HEALTHCHECK: the aspnet runtime image ships neither curl nor wget, so any such
# instruction would be a check that silently always fails. The application exposes /health/live and
# /health/ready instead, and the orchestrator probes them over HTTP — which is how Azure Container
# Apps, App Service and Kubernetes do it anyway. See docs/deployment.md.

ENTRYPOINT ["dotnet", "IelBexio.Web.dll"]
