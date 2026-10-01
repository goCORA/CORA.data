using CORA.Data.Email;

namespace CORA.Data.Tests.Email;

public class JunkFolderDetectionTests
{
   private static JunkCandidate C(string name, bool flag = false, bool topLevel = true, string? fullName = null) =>
      new(fullName ?? name, name, flag, topLevel);

   private static string? Pick(params JunkCandidate[] candidates) =>
      JunkFolderDetection.Pick(candidates)?.FullName;

   [Fact]
   public void Server_without_special_use_support_and_a_folder_named_Junk_Mail_is_detected()
   {
      // No \Junk flag anywhere: only the name can identify it.
      var picked = Pick(C("INBOX"), C("Work"), C("Junk Mail"), C("Archive"));

      Assert.Equal("Junk Mail", picked);
   }

   [Theory]
   [InlineData("Junk")]
   [InlineData("junk")]
   [InlineData("Junk Mail")]
   [InlineData("Junk Email")]
   [InlineData("Junk E-Mail")]
   [InlineData("junk_email")]
   [InlineData("JUNK-MAIL")]
   [InlineData("Spam")]
   [InlineData("Bulk")]
   [InlineData("Bulk Mail")]
   [InlineData("Courrier indésirable")]
   [InlineData("Courrier indesirable")]
   [InlineData("Pourriel")]
   [InlineData("Correo no deseado")]
   [InlineData("Correo basura")]
   public void Known_names_match_ignoring_case_spaces_hyphens_underscores_and_accents(string name) =>
      Assert.Equal(name, Pick(C("INBOX"), C(name)));

   [Theory]
   [InlineData("Junkyard")]
   [InlineData("Spam folder")]
   [InlineData("Receipts")]
   [InlineData("Archive")]
   public void Other_names_do_not_match(string name) =>
      Assert.Null(Pick(C("INBOX"), C(name)));

   [Fact]
   public void No_candidates_gives_null() => Assert.Null(Pick());

   [Fact]
   public void The_junk_flag_matches_whatever_the_folder_is_called()
   {
      var picked = Pick(C("INBOX"), C("Unerwünscht", flag: true));

      Assert.Equal("Unerwünscht", picked);
   }

   [Fact]
   public void The_flag_beats_an_exact_name_match()
   {
      var picked = Pick(C("Junk"), C("Spam", flag: true, topLevel: false, fullName: "[Gmail]/Spam"));

      Assert.Equal("[Gmail]/Spam", picked);
   }

   [Fact]
   public void An_exact_name_beats_a_top_level_folder_with_a_looser_name()
   {
      var picked = Pick(C("junk_mail"), C("Junk Mail", topLevel: false, fullName: "Mail/Junk Mail"));

      Assert.Equal("Mail/Junk Mail", picked);
   }

   [Fact]
   public void A_top_level_folder_beats_a_nested_one_when_otherwise_equal()
   {
      var picked = Pick(C("Spam", topLevel: false, fullName: "Old/Spam"), C("Spam"));

      Assert.Equal("Spam", picked);
   }

   [Fact]
   public void With_nothing_to_choose_between_the_first_listed_wins()
   {
      var picked = Pick(C("Bulk"), C("Spam"));

      Assert.Equal("Bulk", picked);
   }

   [Fact]
   public void Normalize_drops_case_spaces_hyphens_underscores_and_accents()
   {
      Assert.Equal("junkemail", JunkFolderDetection.Normalize("Junk E-Mail"));
      Assert.Equal("courrierindesirable", JunkFolderDetection.Normalize("Courrier indésirable"));
      Assert.Equal("junkemail", JunkFolderDetection.Normalize("  JUNK_email "));
   }
}
