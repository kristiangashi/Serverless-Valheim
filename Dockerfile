# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Coordinator/Coordinator.csproj src/Coordinator/
RUN dotnet restore src/Coordinator/Coordinator.csproj
COPY . .
RUN dotnet publish src/Coordinator/Coordinator.csproj -c Release -o /app

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Railway injects $PORT; the app binds to it automatically. 8080 is the local default.
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Coordinator.dll"]
