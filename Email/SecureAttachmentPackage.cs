using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace CORA.Data.Email;

/// <summary>
/// The plaintext of a secure email's attachments (claude/design-secure-email-attachments.md), before
/// CORA encrypts it into <see cref="FileName"/> with the email's own message key:
/// <code>
/// [6]   magic "CATT1\0"
/// [8]   zip length, little-endian
/// [zip] a standard zip; entries are the bare file names; Deflate for types that compress, Stored for the rest
/// [0…]  zeros up to the next 64 KiB multiple of the whole package
/// </code>
/// One package per email hides the names, types and number of files from the mail provider. The
/// padding hides small differences in size. Reading never trusts the package: at most
/// <see cref="MaxFiles"/> files and <see cref="MaxExpandedBytes"/> expanded in total (counted while
/// expanding), names reduced to safe bare file names, nested archives not opened.
/// </summary>
public static class SecureAttachmentPackage
{
    /// <summary>The name of the one encrypted attachment a secure email carries.</summary>
    public const string FileName = "secure-attachments.cora";

    /// <summary>The package's limit, after compression and padding: 18 MB, so that with email's Base64
    /// growth the email still fits the 25 MB cap of Gmail and Outlook.</summary>
    public const long MaxPackageBytes = 18L * 1024 * 1024;

    /// <summary>Unpacking limits (a "zip bomb" can't get past them).</summary>
    public const int MaxFiles = 100;
    public const long MaxExpandedBytes = 50L * 1024 * 1024;

    /// <summary>The package is padded to a multiple of this.</summary>
    public const int PaddingBlock = 64 * 1024;

    private static readonly byte[] Magic = "CATT1\0"u8.ToArray();
    private const int HeaderBytes = 6 + 8;
    private const int MaxNameLength = 120;

    // Types that usually shrink a lot. Everything else (photos, video, PDF, .docx/.xlsx/.pptx, archives)
    // is already compressed and is stored as it is, which saves phones time and battery for nothing.
    private static readonly HashSet<string> Compressible = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".log", ".json", ".xml", ".html", ".htm", ".md", ".rtf", ".sql", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".eml", ".ics", ".vcf", ".svg", ".ps", ".doc", ".xls", ".ppt", ".bmp", ".tif",
        ".tiff", ".wav", ".pcx", ".tga",
    };

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True when a file of this name is compressed in the package.</summary>
    public static bool ShouldCompress(string fileName) => Compressible.Contains(Path.GetExtension(fileName ?? string.Empty));

    /// <summary>
    /// Builds the package from <paramref name="files"/> (display name, path on this device) into
    /// <paramref name="destPath"/> and returns its size. Throws <see cref="SecureAttachmentsTooLargeException"/>
    /// when it would exceed <see cref="MaxPackageBytes"/> (nothing is left behind).
    /// </summary>
    public static long Build(IReadOnlyList<(string Name, string Path)> files, string destPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            throw new ArgumentException("A secure attachments package needs at least one file.", nameof(files));
        if (files.Count > MaxFiles)
            throw new SecureAttachmentsTooLargeException(0, MaxPackageBytes, tooManyFiles: true);

        var zipPath = destPath + ".zip.tmp";
        try
        {
            using (var zipFile = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, path) in files)
                {
                    var safe = Unique(SafeFileName(name), used);
                    var entry = zip.CreateEntry(safe, ShouldCompress(safe) ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                    using var input = File.OpenRead(path);
                    using var output = entry.Open();
                    input.CopyTo(output);
                }
            }

            var zipLength = new FileInfo(zipPath).Length;
            var total = Padded(HeaderBytes + zipLength);
            if (total > MaxPackageBytes)
                throw new SecureAttachmentsTooLargeException(total, MaxPackageBytes, tooManyFiles: false);

            using (var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Span<byte> header = stackalloc byte[HeaderBytes];
                Magic.CopyTo(header);
                BinaryPrimitives.WriteInt64LittleEndian(header[6..], zipLength);
                output.Write(header);
                using (var zipIn = File.OpenRead(zipPath))
                    zipIn.CopyTo(output);
                WriteZeros(output, total - HeaderBytes - zipLength);
            }
            return total;
        }
        catch
        {
            TryDelete(destPath);
            throw;
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    /// <summary>
    /// Unpacks a decrypted package into <paramref name="folder"/> (created if needed) and returns the
    /// files, in package order. Throws <see cref="InvalidDataException"/> for anything that isn't a
    /// well-formed package within the limits; whatever was written by then is removed.
    /// </summary>
    public static IReadOnlyList<SecureAttachmentFile> Extract(string packagePath, string folder)
    {
        Directory.CreateDirectory(folder);
        var written = new List<SecureAttachmentFile>();
        try
        {
            using var input = File.OpenRead(packagePath);
            Span<byte> header = stackalloc byte[HeaderBytes];
            if (input.Length < HeaderBytes || input.ReadAtLeast(header, HeaderBytes, throwOnEndOfStream: false) != HeaderBytes
                || !header[..6].SequenceEqual(Magic))
                throw new InvalidDataException("This is not a goCORA secure attachments package.");
            var zipLength = BinaryPrimitives.ReadInt64LittleEndian(header[6..]);
            if (zipLength <= 0 || zipLength > input.Length - HeaderBytes)
                throw new InvalidDataException("The secure attachments package is damaged.");

            using var zip = new ZipArchive(new SliceStream(input, HeaderBytes, zipLength), ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
            var files = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
            if (files.Count > MaxFiles)
                throw new InvalidDataException($"The secure attachments package holds more than {MaxFiles} files.");

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            var buffer = new byte[81920];
            foreach (var entry in files)
            {
                var name = Unique(SafeFileName(entry.FullName), used);
                var path = Path.Combine(folder, name);
                long size = 0;
                using (var source = entry.Open())
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    written.Add(new SecureAttachmentFile(name, path, 0));
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        // Counted while expanding: the sizes written in the package aren't trusted.
                        expanded += read;
                        if (expanded > MaxExpandedBytes)
                            throw new InvalidDataException("The secure attachments are larger than goCORA allows when unpacked.");
                        output.Write(buffer, 0, read);
                        size += read;
                    }
                }
                written[^1] = new SecureAttachmentFile(name, path, size);
            }
            return written;
        }
        catch (Exception ex)
        {
            foreach (var f in written)
                TryDelete(f.Path);
            if (ex is InvalidDataException)
                throw;
            throw new InvalidDataException("The secure attachments package is damaged.", ex);
        }
    }

    /// <summary>
    /// A file name that is safe to create on Windows, Android and iOS: the last part of any path,
    /// without characters those systems refuse, without trailing dots or spaces, not a reserved
    /// Windows device name, at most 120 characters (keeping the extension). Never empty.
    /// Used for normal attachments too: a name chosen by the sender is never used as a path.
    /// </summary>
    public static string SafeFileName(string? name)
    {
        var s = name ?? string.Empty;
        var cut = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\'));
        if (cut >= 0)
            s = s[(cut + 1)..];

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0 || s.All(c => c == '.'))
            s = "file";

        var ext = Path.GetExtension(s);
        var stem = s[..^ext.Length];
        if (ReservedWindowsNames.Contains(stem.TrimEnd()))
            stem = "_" + stem;
        if (ext.Length > 20)
        {
            stem += ext;
            ext = string.Empty;
        }
        if (stem.Length + ext.Length > MaxNameLength)
            stem = stem[..(MaxNameLength - ext.Length)].TrimEnd('.', ' ');
        if (stem.Length == 0)
            return ext.Length > 1 ? ext : "file";   // ".gitignore" stays as it is
        return stem + ext;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var n = 2; !used.Add(candidate); n++)
            candidate = $"{stem} ({n}){ext}";
        return candidate;
    }

    private static long Padded(long length) => (length + PaddingBlock - 1) / PaddingBlock * PaddingBlock;

    private static void WriteZeros(Stream output, long count)
    {
        var zeros = new byte[Math.Min(count, PaddingBlock)];
        while (count > 0)
        {
            var n = (int)Math.Min(count, zeros.Length);
            output.Write(zeros, 0, n);
            count -= n;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A read-only view of part of a seekable stream (the zip inside the package).</summary>
    private sealed class SliceStream(Stream inner, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var left = length - _position;
            if (left <= 0)
                return 0;
            inner.Position = start + _position;
            var n = inner.Read(buffer, offset, (int)Math.Min(count, left));
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset,
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>One file unpacked from a secure attachments package.</summary>
public sealed record SecureAttachmentFile(string Name, string Path, long Size)
{
    public string DisplaySize => Size switch
    {
        >= 1024 * 1024 => $"{Size / (1024d * 1024d):0.0} MB",
        >= 1024 => $"{Size / 1024d:0.0} KB",
        _ => $"{Size} B",
    };

    public string Display => $"{Name} ({DisplaySize})";
}

/// <summary>The attachments of a secure email are over the limit (size after compression, or number of files).</summary>
public sealed class SecureAttachmentsTooLargeException(long size, long limit, bool tooManyFiles)
    : Exception(tooManyFiles ? "Too many files for one secure email." : "The attachments are too large for one secure email.")
{
    public long Size { get; } = size;
    public long Limit { get; } = limit;
    public bool TooManyFiles { get; } = tooManyFiles;
}
