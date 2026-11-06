# CodexGate. Two images from one file:
#   docker build -t codexgate .                          the gateway alone (small)
#   docker build -t codexgate-remote --target remote .   gateway + Codex sessions on the server (tmux, Codex, root helper)
# Both listen on 8080 and keep their data in /data; health at / and /healthz.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config CodexGate.slnx ./
COPY src/CodexGate/CodexGate.csproj src/CodexGate/
RUN dotnet restore src/CodexGate/CodexGate.csproj
COPY src/ src/
RUN dotnet publish src/CodexGate/CodexGate.csproj -c Release -o /app --no-restore

# --- gateway + server sessions -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS remote
ARG CODEX_VERSION=0.166.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends tmux sudo nodejs npm git ca-certificates curl less nano \
 && rm -rf /var/lib/apt/lists/* \
 && npm install -g "@openai/codex@${CODEX_VERSION}" \
 && npm cache clean --force \
 && codex --version
WORKDIR /app
COPY --from=build /app ./
COPY deploy/codexgate-host /usr/local/libexec/codexgate-host
# The gateway runs as "app" and may run exactly one thing as root: the helper. /data is the
# gateway's alone (0700), so session accounts cannot read the database or the cookie keys.
RUN chmod 0755 /usr/local/libexec/codexgate-host \
 && printf 'app ALL=(root) NOPASSWD: /usr/local/libexec/codexgate-host\n' > /etc/sudoers.d/codexgate \
 && chmod 0440 /etc/sudoers.d/codexgate \
 && visudo -cf /etc/sudoers.d/codexgate \
 && mkdir -p /data && chown "$APP_UID" /data && chmod 0700 /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Gate__DataDir=/data \
    Gate__Remote__Enabled=true \
    Gate__Remote__GatewayUrl=http://127.0.0.1:8080/v1
EXPOSE 8080
VOLUME ["/data", "/home"]
ENTRYPOINT ["dotnet", "CodexGate.dll"]

# --- gateway only (default) ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS gateway
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /data && chown "$APP_UID" /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Gate__DataDir=/data
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "CodexGate.dll"]
