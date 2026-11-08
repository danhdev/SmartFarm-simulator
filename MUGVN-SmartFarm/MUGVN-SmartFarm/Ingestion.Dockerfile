FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Contracts/SmartFarm.Contracts.csproj Contracts/
COPY Infrastructure/SmartFarm.Infrastructure.csproj Infrastructure/
COPY Ingestion/SmartFarm.Ingestion.csproj Ingestion/
RUN dotnet restore Ingestion/SmartFarm.Ingestion.csproj
COPY . .
RUN dotnet publish Ingestion/SmartFarm.Ingestion.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS tracer
COPY datadog/TracerAssets.csproj /tracer/TracerAssets.csproj
RUN dotnet restore /tracer/TracerAssets.csproj

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
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
ENTRYPOINT ["dotnet", "SmartFarm.Ingestion.dll"]
