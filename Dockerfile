# -------- BUILD --------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

COPY . .
RUN dotnet restore src/HizopePilotage.Api/HizopePilotage.Api.csproj
RUN dotnet publish src/HizopePilotage.Api/HizopePilotage.Api.csproj \
    -c Release \
    -o /out \
    --no-restore

# -------- RUNTIME --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /out .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "HizopePilotage.Api.dll"]
