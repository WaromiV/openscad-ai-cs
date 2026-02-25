using System.Text.Json.Serialization;

namespace c_server.Validation.Models;

/// <summary>Represents a validation issue reported by the CGAL worker.</summary>
/// <param name="Code">Machine-readable warning or error code.</param>
/// <param name="Severity">Severity level for the issue.</param>
/// <param name="Message">Human-readable issue description.</param>
/// <param name="SuggestedFix">Optional remediation guidance.</param>
/// <param name="Evidence">Optional structured evidence payload.</param>
public sealed record ValidationIssue(
  [property: JsonPropertyName("code")] string Code,
  [property: JsonPropertyName("severity")] string Severity,
  [property: JsonPropertyName("message")] string Message,
  [property: JsonPropertyName("suggested_fix")] string? SuggestedFix = null,
  [property: JsonPropertyName("evidence")] Dictionary<string, object?>? Evidence = null
);

/// <summary>Aggregated validation results returned by the CGAL worker.</summary>
/// <param name="Ok">Whether validation succeeded without blocking issues.</param>
/// <param name="Warnings">Collected warning details.</param>
/// <param name="Metrics">Structured metrics emitted by the worker.</param>
/// <param name="Engine">Validation engine identifier.</param>
/// <param name="DurationMs">Elapsed validation time in milliseconds.</param>
public sealed record ValidationReport(
  [property: JsonPropertyName("ok")] bool Ok,
  [property: JsonPropertyName("warnings")] List<ValidationIssue> Warnings,
  [property: JsonPropertyName("metrics")] Dictionary<string, object?> Metrics,
  [property: JsonPropertyName("engine")] string Engine,
  [property: JsonPropertyName("duration_ms")] double DurationMs
);
