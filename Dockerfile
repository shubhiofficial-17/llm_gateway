# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/LlmGateway/LlmGateway.csproj src/LlmGateway/
RUN dotnet restore src/LlmGateway/LlmGateway.csproj
COPY src/ src/
RUN dotnet publish src/LlmGateway/LlmGateway.csproj -c Release -o /app --no-restore

# Run (non-root "app" user ships with the .NET 8 images)
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "LlmGateway.dll"]
