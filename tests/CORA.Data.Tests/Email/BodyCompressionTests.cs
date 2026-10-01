using CORA.Data.Email;

namespace CORA.Data.Tests.Email;

public class BodyCompressionTests
{
   [Fact]
   public void Html_round_trips_through_compression_and_gets_much_smaller()
   {
      var html = string.Concat(Enumerable.Repeat(
         "<table><tr><td style=\"font-family:Arial;color:#333\">Votre relevé est prêt — 42 $ 😀</td></tr></table>\n", 500));

      var packed = MailSyncDatabase.PackText(html)!;

      Assert.True(packed.Length < System.Text.Encoding.UTF8.GetByteCount(html) / 5);
      Assert.Equal(html, MailSyncDatabase.UnpackText(packed, legacy: null));
   }

   [Fact]
   public void Null_and_empty_bodies_are_kept_as_they_are()
   {
      Assert.Null(MailSyncDatabase.PackText(null));
      Assert.Null(MailSyncDatabase.UnpackText(null, legacy: null));
      Assert.Equal(string.Empty, MailSyncDatabase.UnpackText(MailSyncDatabase.PackText(string.Empty), legacy: null));
   }

   [Fact]
   public void Rows_stored_before_compression_are_read_from_the_old_field()
   {
      Assert.Equal("<p>old</p>", MailSyncDatabase.UnpackText(null, legacy: "<p>old</p>"));
   }
}
