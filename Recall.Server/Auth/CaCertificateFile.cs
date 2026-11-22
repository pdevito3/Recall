namespace Recall.Server.Auth;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

/// <summary>
/// Reads the PEM file of a private CA with a bound. A path can name a device or a FIFO, which reports no length and can give
/// bytes with no end or block the read, so only a regular file of at most <see cref="MaxBytes"/> is read.
/// </summary>
public static class CaCertificateFile
{
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The certificates of the file, or null when the path names no file of PEM certificates that this server can read.</summary>
    public static X509Certificate2Collection? Read(string path)
    {
        try
        {
            FileSystemInfo info = new FileInfo(path);
            if (info.LinkTarget is not null)
                info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
            if (info is not FileInfo { Exists: true, Length: > 0 and <= MaxBytes } file)
                return null;

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[MaxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                total += read;
            if (total > MaxBytes)
                return null;

            var certificates = new X509Certificate2Collection();
            certificates.ImportFromPem(Encoding.UTF8.GetString(buffer, 0, total));
            return certificates.Count > 0 ? certificates : null;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
