using c_server.Validation.Models;

namespace c_server.Validation;

public interface ICgalValidationService
{
    ValidationReport Validate(string stlPath, CancellationToken cancellationToken = default);
}
