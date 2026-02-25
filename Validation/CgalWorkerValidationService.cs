using System.Diagnostics;
using System.Text.Json;
using c_server.Validation.Models;

namespace c_server.Validation;

/// <summary>Validates STL meshes via the optional CGAL worker binary.</summary>
public sealed class CgalWorkerValidationService : ICgalValidationService
{
  /// <summary>Absolute path to the server content root used to resolve the worker location.</summary>
  private readonly string contentRoot;
  /// <summary>Maximum time to wait for the CGAL worker to complete.</summary>
  private readonly TimeSpan timeout;

  /// <summary>Creates a new CGAL validation service with an optional timeout override.</summary>
  /// <param name="contentRoot">Content root used to resolve the default worker path.</param>
  /// <param name="timeout">Optional timeout override for worker execution.</param>
  public CgalWorkerValidationService(string contentRoot, TimeSpan? timeout = null)
  {
    this.contentRoot = contentRoot;
    this.timeout = timeout ?? TimeSpan.FromSeconds(90);
  }

  /// <summary>Runs the CGAL worker validation for the provided STL file.</summary>
  /// <param name="stlPath">Absolute path to the STL mesh to validate.</param>
  /// <param name="cancellationToken">Token used to cancel the worker process.</param>
  /// <returns>A validation report describing the worker results.</returns>
  public ValidationReport Validate(string stlPath, CancellationToken cancellationToken = default)
  {
    var started = Stopwatch.StartNew();
    // Resolve and verify the worker binary before launching.
    var workerPath = ResolveWorkerPath();
    if (!File.Exists(workerPath))
    {
      return BuildMissingWorkerReport(started.Elapsed.TotalMilliseconds, workerPath);
    }

    // Start the CGAL worker process and pass the STL path as an argument.
    var psi = new ProcessStartInfo
    {
      FileName = workerPath,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    psi.ArgumentList.Add(stlPath);

    using var process = Process.Start(psi);
    if (process is null)
    {
      return BuildRuntimeErrorReport(
        started.Elapsed.TotalMilliseconds,
        "CGAL worker did not start",
        "WORKER_START_FAILED"
      );
    }

    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeoutCts.CancelAfter(timeout);

    try
    {
      // Wait for completion while honoring the timeout/cancellation token.
      process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();
    }
    catch (OperationCanceledException)
    {
      // Kill the worker if it exceeds the allowed execution time.
      TryKill(process);
      return BuildRuntimeErrorReport(
        started.Elapsed.TotalMilliseconds,
        $"CGAL worker timed out after {timeout.TotalSeconds:0.#}s",
        "CGAL_WORKER_TIMEOUT"
      );
    }

    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();

    // Map non-zero exit codes into a runtime error report.
    if (process.ExitCode != 0)
    {
      var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
      return BuildRuntimeErrorReport(
        started.Elapsed.TotalMilliseconds,
        string.IsNullOrWhiteSpace(detail) ? "CGAL worker failed" : detail.Trim(),
        "CGAL_WORKER_FAILED"
      );
    }

    try
    {
      // Parse the JSON payload produced by the worker.
      using var document = JsonDocument.Parse(stdout);
      var root = document.RootElement;

      var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;

      var warnings = new List<ValidationIssue>();
      if (root.TryGetProperty("warnings", out var warningsElement) && warningsElement.ValueKind == JsonValueKind.Array)
      {
        // Translate worker warning objects into typed issues.
        foreach (var item in warningsElement.EnumerateArray())
        {
          var code = item.TryGetProperty("code", out var codeElement) ? codeElement.GetString() ?? "CGAL_WARNING" : "CGAL_WARNING";
          var severity = item.TryGetProperty("severity", out var severityElement) ? severityElement.GetString() ?? "medium" : "medium";
          var message = item.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? "Validation warning" : "Validation warning";
          var suggestedFix = item.TryGetProperty("suggested_fix", out var suggestedFixElement) ? suggestedFixElement.GetString() : null;

          warnings.Add(new ValidationIssue(code, severity, message, suggestedFix));
        }
      }

      var metrics = new Dictionary<string, object?>();
      if (root.TryGetProperty("metrics", out var metricsElement) && metricsElement.ValueKind == JsonValueKind.Object)
      {
        // Preserve worker metrics with best-effort type conversion.
        foreach (var property in metricsElement.EnumerateObject())
        {
          metrics[property.Name] = JsonElementToObject(property.Value);
        }
      }

      if (stderr.Length > 0)
      {
        // Attach stderr for visibility even when validation succeeds.
        metrics["worker_stderr"] = stderr.Trim();
      }

      var engine = root.TryGetProperty("engine", out var engineElement)
        ? engineElement.GetString() ?? "cgal_worker"
        : "cgal_worker";

      return new ValidationReport(ok, warnings, metrics, engine, started.Elapsed.TotalMilliseconds);
    }
    catch (Exception ex)
    {
      return BuildRuntimeErrorReport(
        started.Elapsed.TotalMilliseconds,
        $"Failed to parse CGAL worker output: {ex.Message}",
        "CGAL_WORKER_OUTPUT_INVALID"
      );
    }
  }

  /// <summary>Resolves the CGAL worker binary path from environment or defaults.</summary>
  /// <returns>Absolute path to the CGAL worker binary.</returns>
  private string ResolveWorkerPath()
  {
    var fromEnv = Environment.GetEnvironmentVariable("CGAL_WORKER_PATH");
    if (!string.IsNullOrWhiteSpace(fromEnv))
    {
      return Path.GetFullPath(fromEnv);
    }

    return Path.Combine(contentRoot, "Validation", "cgal_worker", "bin", "cgal_worker");
  }

  /// <summary>Converts a JSON element into a serializable .NET value.</summary>
  /// <param name="value">JSON value to convert.</param>
  /// <returns>A primitive, list, dictionary, or null value.</returns>
  private static object? JsonElementToObject(JsonElement value)
  {
    // Convert JSON primitives/containers into native .NET representations.
    return value.ValueKind switch
    {
      JsonValueKind.Null => null,
      JsonValueKind.String => value.GetString(),
      JsonValueKind.Number when value.TryGetInt64(out var asLong) => asLong,
      JsonValueKind.Number when value.TryGetDouble(out var asDouble) => asDouble,
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      JsonValueKind.Array => value.EnumerateArray().Select(JsonElementToObject).ToList(),
      JsonValueKind.Object => value.EnumerateObject().ToDictionary(x => x.Name, x => JsonElementToObject(x.Value)),
      _ => value.ToString(),
    };
  }

  /// <summary>Builds a report for missing worker binaries.</summary>
  /// <param name="durationMs">Elapsed duration in milliseconds.</param>
  /// <param name="workerPath">Resolved worker path that is missing.</param>
  /// <returns>A validation report describing the missing worker.</returns>
  private static ValidationReport BuildMissingWorkerReport(double durationMs, string workerPath)
  {
    var warning = new ValidationIssue(
      "CGAL_WORKER_MISSING",
      "medium",
      $"CGAL worker binary not found at {workerPath}",
      "Build the cgal_worker binary and place it in Validation/cgal_worker/bin or set CGAL_WORKER_PATH"
    );
    return new ValidationReport(
      false,
      new List<ValidationIssue> { warning },
      new Dictionary<string, object?> { ["worker_path"] = workerPath },
      "cgal_worker",
      durationMs
    );
  }

  /// <summary>Builds a report for runtime errors emitted by the worker.</summary>
  /// <param name="durationMs">Elapsed duration in milliseconds.</param>
  /// <param name="message">Human-readable error message.</param>
  /// <param name="code">Machine-readable error code.</param>
  /// <returns>A validation report describing the runtime error.</returns>
  private static ValidationReport BuildRuntimeErrorReport(double durationMs, string message, string code)
  {
    var warning = new ValidationIssue(code, "high", message);
    return new ValidationReport(
      false,
      new List<ValidationIssue> { warning },
      new Dictionary<string, object?>(),
      "cgal_worker",
      durationMs
    );
  }

  /// <summary>Attempts to terminate a running worker process, ignoring failures.</summary>
  /// <param name="process">Process instance to terminate.</param>
  private static void TryKill(Process process)
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
      }
    }
    catch
    {
    }
  }
}
