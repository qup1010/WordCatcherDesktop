using System.Text;

namespace WordCatcher.Infrastructure.Storage;

internal static class AtomicFileWriter
{
    public static Task WriteTextAsync(string path, string content, CancellationToken ct = default, bool createBackup = true)
        => WriteBytesAsync(path, Encoding.UTF8.GetBytes(content), ct, createBackup);

    public static async Task WriteBytesAsync(string path, byte[] content, CancellationToken ct = default, bool createBackup = true)
    {
        var fullPath = Path.GetFullPath(path);
        var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            if (createBackup && File.Exists(fullPath))
                File.Replace(temp, fullPath, fullPath + ".bak");
            else
                File.Move(temp, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
