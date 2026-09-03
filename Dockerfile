FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8989
EXPOSE 2222

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
# NuGet Version must be a valid semver (e.g. 0.1.0). Git labels go in InformationalVersion.
ARG VERSION=0.1.0
ARG INFORMATIONAL_VERSION=0.1.0
WORKDIR /src
COPY ["FeatherQuilld.csproj", "./"]
COPY ["FeatherQuilld.Plugins/FeatherQuilld.Plugins.csproj", "FeatherQuilld.Plugins/"]
RUN dotnet restore "FeatherQuilld.csproj"
COPY . .
RUN dotnet publish "./FeatherQuilld.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false \
    --no-restore \
    -p:Version=${VERSION} \
    -p:InformationalVersion=${INFORMATIONAL_VERSION}

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "FeatherQuilld.dll"]
