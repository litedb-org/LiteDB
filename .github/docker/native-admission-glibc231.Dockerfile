FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet
# This image supplies Debian 11/glibc 2.31 and native dependencies only.
# The SDK and managed runtime below are .NET 8, not .NET 6.
FROM mcr.microsoft.com/dotnet/runtime-deps:6.0-bullseye-slim
COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet PATH=/usr/share/dotnet:$PATH DOTNET_CLI_HOME=/tmp/dotnet-cli DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
