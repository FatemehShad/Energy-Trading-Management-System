FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY global.json EnergyTrading.sln ./
COPY src/EnergyTrading.Api/EnergyTrading.Api.csproj src/EnergyTrading.Api/
RUN dotnet restore src/EnergyTrading.Api/EnergyTrading.Api.csproj
COPY src/ src/
RUN dotnet publish src/EnergyTrading.Api/EnergyTrading.Api.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "EnergyTrading.Api.dll"]
