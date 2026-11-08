using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Datadog.Trace;

namespace SmartFarm.Infrastructure.Redis;

/// <summary>Optional transport metadata; never part of the browser or persistence contract.</summary>
public static partial class RedisTraceMessage
{
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "traceparent", "tracestate", "x-datadog-trace-id", "x-datadog-parent-id",
        "x-datadog-sampling-priority", "x-datadog-origin", "x-datadog-tags"
    };

    public static string Serialize<T>(T value, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (headers is null)
        {
            var captured = new Dictionary<string, string>();
            if (Tracer.Instance.ActiveScope is { } scope)
                new SpanContextInjector().Inject(captured, (carrier, key, text) => carrier[key] = text,
                    scope.Span.Context);
            headers = captured;
        }

        var payload = JsonSerializer.SerializeToNode(value, JsonDefaults.Options) as JsonObject
                      ?? throw new JsonException("Redis notifications must be JSON objects");
        var metadata = new JsonObject();
        foreach (var (key, text) in headers)
            if (AllowedHeaders.Contains(key) && text.Length <= 1024)
                metadata[key.ToLowerInvariant()] = text;
        if (metadata.Count != 0) payload["_trace"] = metadata;
        return payload.ToJsonString(JsonDefaults.Options);
    }

    public static RedisNotification<T> Deserialize<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Deserialize<T>(JsonDefaults.Options), ReadHeaders(document.RootElement));
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(JsonElement root)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("_trace", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (!AllowedHeaders.Contains(property.Name)) continue;
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 1024)
                {
                    headers.Clear();
                    break;
                }
                headers[property.Name] = property.Value.GetString()!;
            }
        }

        if (headers.TryGetValue("traceparent", out var parent) &&
            (!TraceParentPattern().IsMatch(parent) || parent.Substring(3, 32).All(c => c == '0') ||
             parent.Substring(36, 16).All(c => c == '0')))
            headers.Clear();

        return headers;
    }

    public static IScope StartConsumer(IReadOnlyDictionary<string, string> headers)
    {
        var parent = new SpanContextExtractor().Extract(headers,
            (carrier, key) => carrier.TryGetValue(key, out var value) ? new[] { value } : Array.Empty<string>());
        var scope = Tracer.Instance.StartActive("redis.notification.consume",
            new SpanCreationSettings { Parent = parent ?? SpanContext.None });
        scope.Span.SetTag("span.kind", "consumer");
        scope.Span.SetTag("messaging.system", "redis");
        return scope;
    }

    [GeneratedRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceParentPattern();
}

public sealed record RedisNotification<T>(T? Value, IReadOnlyDictionary<string, string> Headers);
