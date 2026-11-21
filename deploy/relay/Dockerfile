FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/CommandTray.Relay/CommandTray.Relay.csproj src/CommandTray.Relay/
RUN dotnet restore src/CommandTray.Relay/CommandTray.Relay.csproj
COPY src/CommandTray.Relay/ src/CommandTray.Relay/
RUN dotnet publish src/CommandTray.Relay/CommandTray.Relay.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN install -d -o app -g app /data
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    COMMANDTRAY_RELAY_DATA=/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "CommandTray.Relay.dll"]
