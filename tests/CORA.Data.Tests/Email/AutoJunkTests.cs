using CORA.Data.Email;

namespace CORA.Data.Tests.Email;

public class AutoJunkTests
{
   private static StoredMailSummary Msg(string uidl, string messageId) =>
      new() { Uidl = uidl, Uid = uint.Parse(uidl), From = "spam@example.com", MessageId = messageId };

   [Theory]
   [InlineData("INBOX")]
   [InlineData("Inbox")]
   public void Auto_junk_looks_at_the_Inbox(string folder) =>
      Assert.True(MailKitEmailService.IsAutoJunkFolder(folder));

   [Theory]
   [InlineData("Work")]
   [InlineData("Receipts")]
   [InlineData("Junk")]
   [InlineData("Trash")]
   [InlineData("Sent")]
   [InlineData("Drafts")]
   [InlineData("[Gmail]/Spam")]
   [InlineData("INBOX/Sub")]
   public void Auto_junk_never_touches_any_other_folder(string folder) =>
      Assert.False(MailKitEmailService.IsAutoJunkFolder(folder));

   [Fact]
   public void Rescued_messages_are_excluded_from_auto_junk()
   {
      var blacklisted = new[] { Msg("1", "a@x"), Msg("2", "b@x"), Msg("3", "c@x") };

      var toMove = MailKitEmailService.ExcludeRescued(blacklisted, new HashSet<string> { "b@x" });

      Assert.Equal(["1", "3"], toMove.Select(s => s.Uidl));
   }

   [Fact]
   public void Rescued_ids_match_with_or_without_angle_brackets_and_spaces()
   {
      var blacklisted = new[] { Msg("1", "<a@x>"), Msg("2", " b@x ") };

      var toMove = MailKitEmailService.ExcludeRescued(blacklisted, new HashSet<string> { "a@x", "b@x" });

      Assert.Empty(toMove);
   }

   [Fact]
   public void Messages_without_a_message_id_are_never_treated_as_rescued()
   {
      var blacklisted = new[] { Msg("1", "") };

      var toMove = MailKitEmailService.ExcludeRescued(blacklisted, new HashSet<string> { "a@x" });

      Assert.Single(toMove);
   }

   [Theory]
   [InlineData("INBOX", false, true)]
   [InlineData("inbox", false, true)]
   [InlineData("INBOX", true, false)]      // the AI moved it: not the user's decision
   [InlineData("Work", false, false)]      // auto-junk never looks outside the Inbox
   [InlineData("Junk", false, false)]
   [InlineData("[Gmail]/Spam", false, false)]
   public void Only_a_user_made_move_into_the_Inbox_is_remembered(string destination, bool initiatedByAi, bool expected) =>
      Assert.Equal(expected, MailKitEmailService.ShouldRecordRescue(destination, initiatedByAi));
}
