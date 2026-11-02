using CORA.Data.Email;

namespace CORA.Data.Tests.Email;

public class SecureMessageFormatTests
{
   private const string KeyId = "dde1d5bf8bb54f519b19444a380a5126";

   [Fact]
   public void Build_Then_TryParse_RoundTrips_And_Keeps_The_Agreed_Layout()
   {
      var payload = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
      var body = SecureMessageFormat.Build(KeyId, payload);

      var lines = body.Split("\r\n");
      Assert.Equal(SecureMessageFormat.NoticeLine, lines[0]);
      Assert.Equal(SecureMessageFormat.SeparatorLine, lines[1]);
      Assert.Equal(SecureMessageFormat.BeginMarker, lines[3]);
      Assert.Contains(SecureMessageFormat.EndMarker, lines);
      Assert.All(lines, l => Assert.True(l.Length <= SecureMessageFormat.LineLength || l == SecureMessageFormat.SeparatorLine || l == SecureMessageFormat.NoticeLine));

      Assert.True(SecureMessageFormat.TryParse(body, out var keyId, out var senderId, out var parsed));
      Assert.Equal(KeyId, keyId);
      Assert.Equal(string.Empty, senderId);
      Assert.Equal(payload, parsed);
   }

   [Fact]
   public void New_Emails_Show_No_Key_Id_Or_Sender_Id_Lines()
   {
      var body = SecureMessageFormat.Build(KeyId, [9, 8, 7]);
      Assert.DoesNotContain("Key-Id", body);
      Assert.DoesNotContain("Sender-Id", body);
      Assert.DoesNotContain(KeyId, body, StringComparison.OrdinalIgnoreCase);
   }

   [Fact]
   public void Build_Requires_A_32_Hex_Key_Id()
   {
      Assert.Throws<ArgumentException>(() => SecureMessageFormat.Build("abc123", [1]));
   }

   [Fact]
   public void TryParse_Ignores_Changed_Notice_Wording_And_Quoting_Whitespace()
   {
      var body = SecureMessageFormat.Build(KeyId, [1, 2, 3, 4, 5]).Replace(SecureMessageFormat.NoticeLine, "Some other wording")
         .Replace("\r\n", "\n  ");
      Assert.True(SecureMessageFormat.TryParse("Hi,\n\n" + body, out var keyId, out var parsed));
      Assert.Equal(KeyId, keyId);
      Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, parsed);
   }

   [Fact]
   public void Older_Emails_With_Readable_Lines_Are_Still_Read()
   {
      var old = "notice\r\n\r\n-----BEGIN CORA MESSAGE-----\r\nKey-Id: k2\r\nSender-Id: QUJDREVGR0hJSktMTU5PUA\r\n\r\n"
         + Convert.ToBase64String(new byte[] { 9, 8, 7 }) + "\r\n-----END CORA MESSAGE-----\r\n";
      Assert.True(SecureMessageFormat.TryParse(old, out var keyId, out var senderId, out var parsed));
      Assert.Equal("k2", keyId);
      Assert.Equal("QUJDREVGR0hJSktMTU5PUA", senderId);
      Assert.Equal(new byte[] { 9, 8, 7 }, parsed);
   }

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("plain email")]
   [InlineData("-----BEGIN CORA MESSAGE-----\r\nKey-Id: x\r\nAAAA\r\n")]               // no END
   [InlineData("-----BEGIN CORA MESSAGE-----\r\nAAAA\r\n-----END CORA MESSAGE-----")] // no Key-Id (no header, no line)
   [InlineData("-----BEGIN CORA MESSAGE-----\r\nKey-Id: x\r\n%%%\r\n-----END CORA MESSAGE-----")]
   public void TryParse_Rejects_Incomplete_Blocks(string? body)
   {
      Assert.False(SecureMessageFormat.TryParse(body, out _, out _));
   }

   [Fact]
   public void SplitAddresses_Normalizes_And_Removes_Duplicates()
   {
      var list = SecureMessageFormat.SplitAddresses("Anna <Anna@Example.com>; mike@x.org, anna@example.com ,");
      Assert.Equal(new[] { "anna@example.com", "mike@x.org" }, list);
   }
}
