# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY JiKeDa.Catalog.Api.csproj ./
RUN dotnet restore JiKeDa.Catalog.Api.csproj
COPY . ./
RUN dotnet publish JiKeDa.Catalog.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
USER root
RUN apk upgrade --no-cache && apk add --no-cache bash
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "JiKeDa.Catalog.Api.dll"]
