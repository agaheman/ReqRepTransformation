using Microsoft.Extensions.Options;
using ReqRepTransformation.Core.Abstractions;
using ReqRepTransformation.Core.Models;

namespace ReqRepTransformation.Core.Infrastructure.Redaction;

/// <summary>
/// Default redaction policy driven by PipelineOptions.RedactedHeaderKeys and
/// PipelineOptions.RedactedQueryKeys. All comparisons are case-insensitive.
/// </summary>
public sealed class DefaultRedactionPolicy : IRedactionPolicy
{
    private const string RedactedValue = "***REDACTED***";

    private readonly HashSet<string> _redactedKeys;

    public DefaultRedactionPolicy(IOptions<PipelineOptions> options)
    {
        _redactedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in options.Value.RedactedHeaderKeys)
            _redactedKeys.Add(key);

        foreach (var key in options.Value.RedactedQueryKeys)
            _redactedKeys.Add(key);
    }

    public bool ShouldRedact(string key)
        => _redactedKeys.Contains(key);

    public string Redact(string key, string value)
        => RedactedValue;
}
