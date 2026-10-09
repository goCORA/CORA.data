using CORA.Data.Email;

namespace CORA.Data.Tests.Email;

public class SecureMessageFormatTests
{
   [Fact]
   public void Build_Then_TryParse_RoundTrips_And_Keeps_The_Agreed_Layout()
   {
      var payload = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
      var body = SecureMessageFormat.Build("abc123", payload);

      var lines = body.Split("\r\n");
      Assert.Equal(SecureMessageFormat.NoticeLine, lines[0]);
      Assert.Equal(SecureMessageFormat.SeparatorLine, lines[1]);
      Assert.Equal(SecureMessageFormat.BeginMarker, lines[3]);
      Assert.Equal("Key-Id: abc123", lines[4]);
      Assert.Contains(SecureMessageFormat.EndMarker, lines);
      Assert.All(lines, l => Assert.True(l.Length <= SecureMessageFormat.LineLength || l == SecureMessageFormat.SeparatorLine || l == SecureMessageFormat.NoticeLine));

      Assert.True(SecureMessageFormat.TryParse(body, out var keyId, out var parsed));
      Assert.Equal("abc123", keyId);
      Assert.Equal(payload, parsed);
   }

   [Fact]
   public void TryParse_Ignores_Changed_Notice_Wording_And_Quoting_Whitespace()
   {
      var body = SecureMessageFormat.Build("k1", [1, 2, 3, 4, 5]).Replace(SecureMessageFormat.NoticeLine, "Some other wording")
         .Replace("\r\n", "\n  ");
      Assert.True(SecureMessageFormat.TryParse("Hi,\n\n" + body, out var keyId, out var parsed));
      Assert.Equal("k1", keyId);
      Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, parsed);
   }

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("plain email")]
   [InlineData("-----BEGIN CORA MESSAGE-----\r\nKey-Id: x\r\nAAAA\r\n")]               // no END
   [InlineData("-----BEGIN CORA MESSAGE-----\r\nAAAA\r\n-----END CORA MESSAGE-----")] // no Key-Id
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
