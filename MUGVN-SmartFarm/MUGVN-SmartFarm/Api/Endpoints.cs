using FastEndpoints;
using SmartFarm.Contracts.Devices;
using SmartFarm.Contracts.History;
using SmartFarm.Contracts.Telemetry;
using MUGVN_SmartFarm.Application.Devices;
using MUGVN_SmartFarm.Application.Telemetry;

namespace MUGVN_SmartFarm.Api;

public sealed class DeviceListEndpoint(DeviceService service) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/devices");
        Policies("FarmAdmin");
        Description(x =>
            x.WithSummary("List registered devices and observed state").Produces<IReadOnlyList<DeviceDto>>());
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await HttpContext.Response.WriteAsJsonAsync(await service.ListAsync(ct), JsonDefaults.Options, ct);
}

public sealed class DeviceGetEndpoint(DeviceService service) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/devices/{deviceId}");
        Policies("FarmAdmin");
        Description(x => x.WithSummary("Get one registered device").Produces<DeviceDto>());
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await HttpContext.Response.WriteAsJsonAsync(await service.GetAsync(Route<string>("deviceId")!, ct),
            JsonDefaults.Options, ct);
}

public sealed class DeviceCreateEndpoint(DeviceService service) : Endpoint<CreateDeviceRequest>
{
    public override void Configure()
    {
        Post("/api/devices");
        Policies("FarmAdmin");
        Description(x => x.WithSummary("Register a device").Produces<DeviceDto>(201));
    }

    public override async Task HandleAsync(CreateDeviceRequest req, CancellationToken ct)
    {
        var d = await service.CreateAsync(new CreateDeviceCommand(req.DeviceId, req.NodeId, req.Label, req.ReportingIntervalSeconds), ct);
        HttpContext.Response.StatusCode = 201;
        HttpContext.Response.Headers.Location = $"/api/devices/{d.DeviceId}";
        await HttpContext.Response.WriteAsJsonAsync(d, JsonDefaults.Options, ct);
    }
}

public sealed class DevicePatchEndpoint(DeviceService service) : Endpoint<PatchDeviceRequest>
{
    public override void Configure()
    {
        Patch("/api/devices/{deviceId}");
        Policies("FarmAdmin");
        Description(x => x.WithSummary("Edit device metadata").Produces<DeviceDto>());
    }

    public override async Task HandleAsync(PatchDeviceRequest req, CancellationToken ct) =>
        await HttpContext.Response.WriteAsJsonAsync(await service.PatchAsync(Route<string>("deviceId")!, new UpdateDeviceCommand(req.Label, req.ReportingIntervalSeconds, req.Enabled), ct),
            JsonDefaults.Options, ct);
}

public sealed class DeviceDeleteEndpoint(DeviceService service) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/devices/{deviceId}");
        Policies("FarmAdmin");
        Description(x => x.WithSummary("Decommission a device, preserving history").Produces(204));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await service.DeleteAsync(Route<string>("deviceId")!, ct);
        HttpContext.Response.StatusCode = 204;
    }
}

public sealed class LatestEndpoint(TelemetryQueryService service) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/telemetry/latest");
        Policies("FarmAdmin");
        Description(x =>
            x.WithSummary("Newest source-time reading per device").Produces<IReadOnlyList<TelemetryDto>>());
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await HttpContext.Response.WriteAsJsonAsync(await service.LatestAsync(Query<string>("deviceIds", false), ct),
            JsonDefaults.Options, ct);
}

public sealed class HistoryEndpoint(TelemetryQueryService service) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/devices/{deviceId}/telemetry/history");
        Policies("FarmAdmin");
        Description(x => x.WithSummary("Source-time range [from,to), cursor-paginated").Produces<HistoryPage>());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var q = new HistoryQuery(Route<string>("deviceId")!, Query<DateTimeOffset>("from"), Query<DateTimeOffset>("to"),
            Query<int?>("limit", false) ?? 100, Query<string>("cursor", false));
        await HttpContext.Response.WriteAsJsonAsync(await service.HistoryAsync(q, ct), JsonDefaults.Options, ct);
    }
}

public sealed class ReceivedHistoryEndpoint(TelemetryQueryService service) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/devices/{deviceId}/telemetry/received-history");
        Policies("FarmAdmin");
        Description(x => x.WithSummary("Readings received in [from,to), cursor-paginated by ingestion time")
            .Produces<HistoryPage>());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var q = new ReceivedHistoryQuery(Route<string>("deviceId")!, Query<DateTimeOffset>("from"),
            Query<DateTimeOffset>("to"), Query<int?>("limit", false) ?? 100,
            Query<string>("cursor", false));
        await HttpContext.Response.WriteAsJsonAsync(await service.ReceivedHistoryAsync(q, ct), JsonDefaults.Options,
            ct);
    }
}

public sealed class AggregatesEndpoint(TelemetryQueryService service) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/devices/{deviceId}/telemetry/aggregates");
        Policies("FarmAdmin");
        Description(x =>
            x.WithSummary("UTC hour/day means excluding missing observations").Produces<IReadOnlyList<AggregateDto>>());
    }

    public override async Task HandleAsync(CancellationToken ct) => await HttpContext.Response.WriteAsJsonAsync(
        await service.AggregatesAsync(Route<string>("deviceId")!, Query<DateTimeOffset>("from"),
            Query<DateTimeOffset>("to"), Query<string>("interval", false) ?? "hour", ct), JsonDefaults.Options, ct);
}
