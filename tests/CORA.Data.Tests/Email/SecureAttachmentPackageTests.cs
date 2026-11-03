using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CORA.Data.Email;

namespace CORA.Data.Tests.Email;

public class SecureAttachmentPackageTests : IDisposable
{
   private readonly string _dir = Path.Combine(Path.GetTempPath(), "cora_att_" + Guid.NewGuid().ToString("N"));

   public SecureAttachmentPackageTests() => Directory.CreateDirectory(_dir);

   public void Dispose()
   {
      try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
   }

   private string WriteFile(string name, byte[] content)
   {
      var path = Path.Combine(_dir, "src_" + Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(name));
      File.WriteAllBytes(path, content);
      return path;
   }

   [Fact]
   public void Build_Then_Extract_RoundTrips_Names_And_Contents_And_Pads()
   {
      var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Quarterly numbers, line after line. ", 5000)));
      var photo = new byte[300_000];
      new Random(7).NextBytes(photo);   // like a JPEG: doesn't compress
      var files = new List<(string, string)>
      {
         ("notes.txt", WriteFile("notes.txt", text)),
         ("photo.jpg", WriteFile("photo.jpg", photo)),
         ("empty.csv", WriteFile("empty.csv", [])),
      };
      var package = Path.Combine(_dir, "package.bin");

      var size = SecureAttachmentPackage.Build(files, package);

      Assert.Equal(new FileInfo(package).Length, size);
      Assert.Equal(0, size % SecureAttachmentPackage.PaddingBlock);
      Assert.True(size < text.Length + photo.Length, "the text was compressed");

      var outDir = Path.Combine(_dir, "out");
      var result = SecureAttachmentPackage.Extract(package, outDir);
      Assert.Equal(["notes.txt", "photo.jpg", "empty.csv"], result.Select(f => f.Name));
      Assert.Equal(text, File.ReadAllBytes(result[0].Path));
      Assert.Equal(photo, File.ReadAllBytes(result[1].Path));
      Assert.Empty(File.ReadAllBytes(result[2].Path));
      Assert.Equal(photo.Length, result[1].Size);
      Assert.All(result, f => Assert.Equal(Path.GetFullPath(outDir), Path.GetDirectoryName(Path.GetFullPath(f.Path))));
   }

   [Fact]
   public void Only_Types_That_Benefit_Are_Compressed()
   {
      Assert.True(SecureAttachmentPackage.ShouldCompress("report.CSV"));
      Assert.True(SecureAttachmentPackage.ShouldCompress("scan.tiff"));
      Assert.False(SecureAttachmentPackage.ShouldCompress("photo.jpg"));
      Assert.False(SecureAttachmentPackage.ShouldCompress("contract.pdf"));
      Assert.False(SecureAttachmentPackage.ShouldCompress("budget.xlsx"));
      Assert.False(SecureAttachmentPackage.ShouldCompress("no-extension"));
   }

   [Fact]
   public void Same_Names_Are_Numbered()
   {
      var files = new List<(string, string)>
      {
         ("a.txt", WriteFile("a.txt", "1"u8.ToArray())),
         ("A.txt", WriteFile("A.txt", "2"u8.ToArray())),
         ("a.txt", WriteFile("a.txt", "3"u8.ToArray())),
      };
      var package = Path.Combine(_dir, "package.bin");
      SecureAttachmentPackage.Build(files, package);
      var result = SecureAttachmentPackage.Extract(package, Path.Combine(_dir, "out"));
      Assert.Equal(["a.txt", "A (2).txt", "a (3).txt"], result.Select(f => f.Name));
   }

   [Fact]
   public void Too_Large_After_Compression_Is_Refused_And_Leaves_Nothing()
   {
      var big = new byte[SecureAttachmentPackage.MaxPackageBytes + 1];
      new Random(1).NextBytes(big);
      var package = Path.Combine(_dir, "package.bin");
      var ex = Assert.Throws<SecureAttachmentsTooLargeException>(() =>
         SecureAttachmentPackage.Build([("video.mp4", WriteFile("video.mp4", big))], package));
      Assert.False(ex.TooManyFiles);
      Assert.True(ex.Size > ex.Limit);
      Assert.False(File.Exists(package));
      Assert.False(File.Exists(package + ".zip.tmp"));
   }

   [Fact]
   public void Large_But_Compressible_Fits()
   {
      // 30 MB of log text compresses far below the 18 MB limit.
      var log = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("2026-11-03 10:00:00 INFO sync ok\n", 950_000)));
      var package = Path.Combine(_dir, "package.bin");
      var size = SecureAttachmentPackage.Build([("server.log", WriteFile("server.log", log))], package);
      Assert.True(size < SecureAttachmentPackage.MaxPackageBytes);
   }

   [Fact]
   public void A_Zip_Bomb_Stops_At_The_Expanded_Limit_And_Leaves_Nothing()
   {
      var package = WritePackage(zip =>
      {
         var entry = zip.CreateEntry("zeros.txt", CompressionLevel.Optimal);
         using var s = entry.Open();
         var zeros = new byte[1024 * 1024];
         for (var i = 0; i < 51; i++)
            s.Write(zeros);
      });
      var outDir = Path.Combine(_dir, "out");
      Assert.Throws<InvalidDataException>(() => SecureAttachmentPackage.Extract(package, outDir));
      Assert.Empty(Directory.GetFiles(outDir));
   }

   [Fact]
   public void Too_Many_Files_Are_Refused()
   {
      var package = WritePackage(zip =>
      {
         for (var i = 0; i <= SecureAttachmentPackage.MaxFiles; i++)
            using (var s = zip.CreateEntry($"f{i}.txt").Open())
               s.WriteByte(1);
      });
      Assert.Throws<InvalidDataException>(() => SecureAttachmentPackage.Extract(package, Path.Combine(_dir, "out")));
   }

   [Fact]
   public void Paths_In_Entry_Names_Never_Leave_The_Folder()
   {
      var package = WritePackage(zip =>
      {
         foreach (var name in new[] { "../../evil.exe", "..\\..\\Windows\\win.ini", "folder/inner.txt", "CON.txt", "a<b>?.txt" })
            using (var s = zip.CreateEntry(name).Open())
               s.WriteByte(1);
         zip.CreateEntry("emptydir/");
      });
      var outDir = Path.Combine(_dir, "out");
      var result = SecureAttachmentPackage.Extract(package, outDir);
      Assert.Equal(["evil.exe", "win.ini", "inner.txt", "_CON.txt", "a_b__.txt"], result.Select(f => f.Name));
      Assert.Equal(5, Directory.GetFiles(outDir).Length);
      Assert.Empty(Directory.GetDirectories(outDir));
   }

   [Theory]
   [InlineData("report.pdf", "report.pdf")]
   [InlineData("C:\\Users\\me\\report.pdf", "report.pdf")]
   [InlineData("/etc/passwd", "passwd")]
   [InlineData("  name. . ", "name")]
   [InlineData("", "file")]
   [InlineData("...", "file")]
   [InlineData(".gitignore", ".gitignore")]
   [InlineData("nul", "_nul")]
   [InlineData("Com1.TXT", "_Com1.TXT")]
   [InlineData("tab\there.txt", "tab_here.txt")]
   public void SafeFileName_Cases(string input, string expected) =>
      Assert.Equal(expected, SecureAttachmentPackage.SafeFileName(input));

   [Fact]
   public void SafeFileName_Caps_Length_And_Keeps_The_Extension()
   {
      var safe = SecureAttachmentPackage.SafeFileName(new string('x', 300) + ".docx");
      Assert.Equal(120, safe.Length);
      Assert.EndsWith(".docx", safe);
   }

   [Fact]
   public void Not_A_Package_Or_A_Damaged_One_Is_Refused()
   {
      var outDir = Path.Combine(_dir, "out");
      var notPackage = WriteFile("x.bin", "PK\u0003\u0004 not ours"u8.ToArray());
      Assert.Throws<InvalidDataException>(() => SecureAttachmentPackage.Extract(notPackage, outDir));

      var tooShort = WriteFile("y.bin", "CATT"u8.ToArray());
      Assert.Throws<InvalidDataException>(() => SecureAttachmentPackage.Extract(tooShort, outDir));

      // A zip length running past the end of the file.
      var header = new byte[14 + 10];
      "CATT1\0"u8.CopyTo(header);
      BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), 5000);
      Assert.Throws<InvalidDataException>(() => SecureAttachmentPackage.Extract(WriteFile("z.bin", header), outDir));

      // Garbage where the zip should be.
      BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), 10);
      Assert.Throws<InvalidDataException>(() => SecureAttachmentPackage.Extract(WriteFile("w.bin", header), outDir));
   }

   /// <summary>A package around a zip written by <paramref name="fill"/> (to test what Extract refuses).</summary>
   private string WritePackage(Action<ZipArchive> fill)
   {
      using var zipStream = new MemoryStream();
      using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
         fill(zip);
      var bytes = zipStream.ToArray();
      var package = new byte[14 + bytes.Length];
      "CATT1\0"u8.CopyTo(package);
      BinaryPrimitives.WriteInt64LittleEndian(package.AsSpan(6), bytes.Length);
      bytes.CopyTo(package, 14);
      return WriteFile("package.bin", package);
   }
}
