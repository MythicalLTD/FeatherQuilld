FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8989
EXPOSE 2222

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.1.0
WORKDIR /src
COPY ["FeatherQuilld.csproj", "./"]
RUN dotnet restore "FeatherQuilld.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "./FeatherQuilld.csproj" -c $BUILD_CONFIGURATION -o /app/build \
    -p:Version=${VERSION} -p:InformationalVersion=${VERSION}

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.1.0
RUN dotnet publish "./FeatherQuilld.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false \
    -p:Version=${VERSION} -p:InformationalVersion=${VERSION}

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "FeatherQuilld.dll"]
