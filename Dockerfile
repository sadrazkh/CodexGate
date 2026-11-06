# CodexGate: one container, SQLite in /data. Listens on 8080; health at / and /healthz.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config CodexGate.slnx ./
COPY src/CodexGate/CodexGate.csproj src/CodexGate/
RUN dotnet restore src/CodexGate/CodexGate.csproj
COPY src/ src/
RUN dotnet publish src/CodexGate/CodexGate.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /data && chown "$APP_UID" /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Gate__DataDir=/data
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "CodexGate.dll"]
