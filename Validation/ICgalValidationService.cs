using c_server.Validation.Models;

namespace c_server.Validation;

/// <summary>Contract for CGAL-backed STL validation services.</summary>
public interface ICgalValidationService
{
  /// <summary>Validates the provided STL mesh and returns a report.</summary>
  /// <param name="stlPath">Absolute path to the STL mesh to validate.</param>
  /// <param name="cancellationToken">Token used to cancel validation.</param>
  /// <returns>A validation report describing any issues found.</returns>
  ValidationReport Validate(string stlPath, CancellationToken cancellationToken = default);
}
