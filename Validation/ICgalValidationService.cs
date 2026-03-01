using c_server.Validation.Models;

namespace c_server.Validation;

/// <summary>Contract for CGAL-backed STL validation services.</summary>
public interface ICgalValidationService
{
  /// <summary>Validates the provided STL mesh and returns a report.</summary>
  /// <param name="stlPath">Absolute path to the STL mesh to validate.</param>
  /// <param name="minWallThicknessMm">Optional minimum wall thickness threshold in millimeters.</param>
  /// <param name="printProcess">Optional print process identifier associated with the thickness threshold.</param>
  /// <param name="cancellationToken">Token used to cancel validation.</param>
  /// <returns>A validation report describing any issues found.</returns>
  ValidationReport Validate(
    string stlPath,
    double? minWallThicknessMm = null,
    string? printProcess = null,
    CancellationToken cancellationToken = default
  );
}
