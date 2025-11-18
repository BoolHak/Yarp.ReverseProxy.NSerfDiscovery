FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 7946

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files
COPY ["TestService/TestService.csproj", "TestService/"]
COPY ["Yarp.ReverseProxy.NSerfDiscovery/Yarp.ReverseProxy.NSerfDiscovery.csproj", "Yarp.ReverseProxy.NSerfDiscovery/"]

# Restore dependencies (NSerf will be restored from NuGet)
RUN dotnet restore "TestService/TestService.csproj"

# Copy source code
COPY TestService/ TestService/
COPY Yarp.ReverseProxy.NSerfDiscovery/ Yarp.ReverseProxy.NSerfDiscovery/

# Build
WORKDIR "/src/TestService"
RUN dotnet build "TestService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "TestService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "TestService.dll"]
