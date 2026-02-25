namespace ReqRepTransformation.Core.Abstractions;

/// <summary>
/// Thin abstraction over request/response header collections.
/// Decouples transforms from IHeaderDictionary (ASP.NET) and
/// HttpRequestHeaders / HttpResponseHeaders (HttpClient).
/// </summary>
public interface IMessageHeaders
{
    /// <summary>All header keys currently present.</summary>
    IEnumerable<string> Keys { get; }

    /// <summary>Returns the first value for <paramref name="key"/>, or null if absent.</summary>
    string? Get(string key);

    /// <summary>Returns all values for <paramref name="key"/>.</summary>
    IEnumerable<string> GetValues(string key);

    /// <summary>Sets (overwrites) the header with a single value.</summary>
    void Set(string key, string value);

    /// <summary>Appends a value to an existing header or creates it.</summary>
    void Append(string key, string value);

    /// <summary>Removes the header entirely. No-op if absent.</summary>
    void Remove(string key);

    /// <summary>Returns true if the header is present with any value.</summary>
    bool Contains(string key);

    /// <summary>Attempts to get value; returns false if absent.</summary>
    bool TryGet(string key, out string? value);
}
