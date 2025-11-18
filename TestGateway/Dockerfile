FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 7946

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files
COPY ["TestGateway/TestGateway.csproj", "TestGateway/"]
COPY ["Yarp.ReverseProxy.NSerfDiscovery/Yarp.ReverseProxy.NSerfDiscovery.csproj", "Yarp.ReverseProxy.NSerfDiscovery/"]

# Restore dependencies (NSerf will be restored from NuGet)
RUN dotnet restore "TestGateway/TestGateway.csproj"

# Copy source code
COPY TestGateway/ TestGateway/
COPY Yarp.ReverseProxy.NSerfDiscovery/ Yarp.ReverseProxy.NSerfDiscovery/

# Build
WORKDIR "/src/TestGateway"
RUN dotnet build "TestGateway.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "TestGateway.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "TestGateway.dll"]
