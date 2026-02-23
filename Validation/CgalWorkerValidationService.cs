using System.Diagnostics;
using System.Text.Json;
using c_server.Validation.Models;

namespace c_server.Validation;

public sealed class CgalWorkerValidationService : ICgalValidationService
{
    private readonly string _contentRoot;
    private readonly TimeSpan _timeout;

    public CgalWorkerValidationService(string contentRoot, TimeSpan? timeout = null)
    {
        _contentRoot = contentRoot;
        _timeout = timeout ?? TimeSpan.FromSeconds(90);
    }

    public ValidationReport Validate(string stlPath, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var workerPath = ResolveWorkerPath();
        if (!File.Exists(workerPath))
        {
            return BuildMissingWorkerReport(started.Elapsed.TotalMilliseconds, workerPath);
        }

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
        timeoutCts.CancelAfter(_timeout);

        try
        {
            process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return BuildRuntimeErrorReport(
                started.Elapsed.TotalMilliseconds,
                $"CGAL worker timed out after {_timeout.TotalSeconds:0.#}s",
                "CGAL_WORKER_TIMEOUT"
            );
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

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
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;

            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;

            var warnings = new List<ValidationIssue>();
            if (root.TryGetProperty("warnings", out var warningsElement) && warningsElement.ValueKind == JsonValueKind.Array)
            {
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
                foreach (var property in metricsElement.EnumerateObject())
                {
                    metrics[property.Name] = JsonElementToObject(property.Value);
                }
            }

            if (stderr.Length > 0)
            {
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

    private string ResolveWorkerPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CGAL_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return Path.Combine(_contentRoot, "Validation", "cgal_worker", "bin", "cgal_worker");
    }

    private static object? JsonElementToObject(JsonElement value)
    {
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
