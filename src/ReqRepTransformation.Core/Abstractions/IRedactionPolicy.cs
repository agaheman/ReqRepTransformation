namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Redaction policy applied to all log and trace output.
/// Enforced at the logging infrastructure layer — individual transforms
/// do not call redaction methods directly.
///
/// The pipeline logs: header keys/values, path segments, query string keys/values,
/// content-type, transform names, and error messages.
/// JWT tokens, API keys, cookies, and PII fields are redacted by default.
/// </summary>
public interface IRedactionPolicy
{
    /// <summary>Returns true if the value associated with this key should be redacted.</summary>
    bool ShouldRedact(string key);

    /// <summary>
    /// Returns the redacted representation of a value.
    /// Default: "***REDACTED***"
    /// Custom implementations may return partial values (e.g., last 4 chars of a token).
    /// </summary>
    string Redact(string key, string value);
}
