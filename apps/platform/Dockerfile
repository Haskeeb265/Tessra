FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for layer caching
COPY ["Directory.Build.props", "."]
COPY ["apps/platform/src/Tessra.Platform.Api/Tessra.Platform.Api.csproj", "apps/platform/src/Tessra.Platform.Api/"]
COPY ["apps/platform/src/Tessra.Platform.Domain/Tessra.Platform.Domain.csproj", "apps/platform/src/Tessra.Platform.Domain/"]
COPY ["apps/platform/src/Tessra.Platform.Observability/Tessra.Platform.Observability.csproj", "apps/platform/src/Tessra.Platform.Observability/"]
RUN dotnet restore "apps/platform/src/Tessra.Platform.Api/Tessra.Platform.Api.csproj"

# Copy all source and build
COPY apps/platform/src/ apps/platform/src/
WORKDIR "/src/apps/platform/src/Tessra.Platform.Api"
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080

# Use non-root user for security
USER app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Tessra.Platform.Api.dll"]
