FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for layer caching
COPY ["Directory.Build.props", "."]
COPY ["apps/platform/src/Tessera.Platform.Api/Tessera.Platform.Api.csproj", "apps/platform/src/Tessera.Platform.Api/"]
COPY ["apps/platform/src/Tessera.Platform.Domain/Tessera.Platform.Domain.csproj", "apps/platform/src/Tessera.Platform.Domain/"]
COPY ["apps/platform/src/Tessera.Platform.Observability/Tessera.Platform.Observability.csproj", "apps/platform/src/Tessera.Platform.Observability/"]
RUN dotnet restore "apps/platform/src/Tessera.Platform.Api/Tessera.Platform.Api.csproj"

# Copy all source and build
COPY apps/platform/src/ apps/platform/src/
WORKDIR "/src/apps/platform/src/Tessera.Platform.Api"
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080

# Dev note: the MCP OAuth keys mount (/app/keys) must be writable by the
# process. On Dev Desktop Linux VM, the named volume gets root ownership,
# so running as the image's 'app' user fails with EACCES at startup.
# For dev we keep this as root; harden (custom entrypoint chown, or fixed uid)
# before any production story.
#
# USER app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Tessera.Platform.Api.dll"]
