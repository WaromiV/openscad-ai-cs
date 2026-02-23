using System.Text.Json.Serialization;

namespace c_server.Validation.Models;

public sealed record ValidationIssue(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("suggested_fix")] string? SuggestedFix = null,
    [property: JsonPropertyName("evidence")] Dictionary<string, object?>? Evidence = null
);

public sealed record ValidationReport(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("warnings")] List<ValidationIssue> Warnings,
    [property: JsonPropertyName("metrics")] Dictionary<string, object?> Metrics,
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("duration_ms")] double DurationMs
);
