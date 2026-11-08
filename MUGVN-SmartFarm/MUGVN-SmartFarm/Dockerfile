FROM node:22-alpine AS demo
WORKDIR /ui
COPY UI/package*.json ./
RUN npm ci
COPY UI/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY MUGVN-SmartFarm.csproj ./
COPY Contracts/SmartFarm.Contracts.csproj Contracts/
COPY Infrastructure/SmartFarm.Infrastructure.csproj Infrastructure/
RUN dotnet restore MUGVN-SmartFarm.csproj
COPY . .
COPY --from=demo /wwwroot/demo/app.js wwwroot/demo/app.js
RUN dotnet publish MUGVN-SmartFarm.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS tracer
COPY datadog/TracerAssets.csproj /tracer/TracerAssets.csproj
RUN dotnet restore /tracer/TracerAssets.csproj

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
COPY --from=tracer /root/.nuget/packages/datadog.trace.bundle/3.57.0/contentFiles/any/any/datadog/linux-x64 /opt/datadog/linux-x64
COPY --from=tracer /root/.nuget/packages/datadog.trace.bundle/3.57.0/contentFiles/any/any/datadog/net6.0 /opt/datadog/net6.0
COPY --from=tracer /root/.nuget/packages/datadog.trace.bundle/3.57.0/contentFiles/any/any/datadog/createLogPath.sh /opt/datadog/createLogPath.sh
RUN /opt/datadog/createLogPath.sh
ENV CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER={846F5F1C-F9AE-4B07-969E-05C26BC060D8} \
    CORECLR_PROFILER_PATH=/opt/datadog/linux-x64/Datadog.Trace.ClrProfiler.Native.so \
    DD_DOTNET_TRACER_HOME=/opt/datadog
USER $APP_UID
ENTRYPOINT ["dotnet", "MUGVN-SmartFarm.dll"]
