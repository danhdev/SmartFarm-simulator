using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MUGVN_SmartFarm.Api;

public static class OpenApiConfiguration
{
    public static void Configure(OpenApiOptions options)
    {
        options.AddOperationTransformer((operation, context, ct) =>
        {
            var path = context.Description.RelativePath ?? "";

            void Parameter(string name, bool required, string description, string? format = null)
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = name, In = ParameterLocation.Query, Required = required, Description = description,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = format }
                });
            }

            if (path.EndsWith("/history", StringComparison.Ordinal) ||
                path.EndsWith("/aggregates", StringComparison.Ordinal))
            {
                Parameter("from", true, "Inclusive source time (UTC offset required)", "date-time");
                Parameter("to", true, "Exclusive source time; range at most 366 days", "date-time");
            }

            if (path.EndsWith("/history", StringComparison.Ordinal))
            {
                Parameter("limit", false, "Page size, 1–1000; default 100");
                Parameter("cursor", false,
                    "Opaque next_cursor from the preceding page, with unchanged device and range");
            }

            if (path.EndsWith("/aggregates", StringComparison.Ordinal))
                Parameter("interval", false, "hour (default) or day; UTC aligned");
            if (path == "api/telemetry/latest")
                Parameter("deviceIds", false,
                    "Comma-separated device identities, at most 100; omitted means all active registrations");
            return Task.CompletedTask;
        });
        options.AddDocumentTransformer((document, context, ct) =>
        {
            document.Components ??= new();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "Keycloak access token with smartfarm-api audience and farm-admin realm role"
            };
            foreach (var path in document.Paths.Where(p => p.Key.StartsWith("/api/", StringComparison.Ordinal)))
            foreach (var operation in path.Value.Operations?.Values.AsEnumerable() ??
                                      Enumerable.Empty<OpenApiOperation>())
                operation.Security =
                    [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
            return Task.CompletedTask;
        });
    }
}