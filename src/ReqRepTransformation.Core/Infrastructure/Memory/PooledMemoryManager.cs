using Microsoft.IO;

namespace ReqRepTransformation.Core.Infrastructure.Memory;

/// <summary>
/// Wraps RecyclableMemoryStreamManager as a process-wide singleton.
/// Provides factory methods for obtaining pooled streams.
///
/// RecyclableMemoryStreamManager prevents LOH pressure by reusing stream objects
/// and byte blocks across requests. Register as Singleton in DI.
///
/// Note: Options API differs by package version — we use only the stable
/// properties available in Microsoft.IO.RecyclableMemoryStream 3.x.
/// </summary>
public static class PooledMemoryManager
{
    private static readonly RecyclableMemoryStreamManager _manager;

    static PooledMemoryManager()
    {
        // Use the settings constructor that is stable across 2.x and 3.x
        // BlockSize=128KB, LargeBufferMultiple=1MB, MaxBufferSize=128MB
        _manager = new RecyclableMemoryStreamManager(
            blockSize:            128  * 1024,
            largeBufferMultiple:  1024 * 1024,
            maximumBufferSize:    128  * 1024 * 1024);
    }

    /// <summary>Returns a pooled MemoryStream. Caller must dispose after use.</summary>
    public static MemoryStream GetStream(string tag = "reqrep")
        => _manager.GetStream(tag);

    /// <summary>Returns a pooled MemoryStream pre-filled with <paramref name="bytes"/>.</summary>
    public static MemoryStream GetStream(ReadOnlySpan<byte> bytes, string tag = "reqrep")
    {
        var stream = _manager.GetStream(tag, bytes.Length);
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    /// <summary>Returns a pooled MemoryStream pre-filled from <paramref name="memory"/>.</summary>
    public static MemoryStream GetStream(ReadOnlyMemory<byte> memory, string tag = "reqrep")
        => GetStream(memory.Span, tag);
}
