using CORA.Data.Email;

namespace CORA.Data.Tests.Email;

public class FolderOrderingTests
{
   private static MailFolderInfo F(string name) => new() { FullName = name, Name = name };

   private static List<MailFolderInfo> Folders(params string[] names) => names.Select(F).ToList();

   private static string[] Names(IEnumerable<MailFolderInfo> folders) => folders.Select(f => f.FullName).ToArray();

   [Fact]
   public void No_saved_order_returns_the_server_order_unchanged()
   {
      var folders = Folders("INBOX", "Zeta", "Alpha", "Sent");

      Assert.Equal(["INBOX", "Zeta", "Alpha", "Sent"], Names(FolderOrdering.Apply(folders, [])));
   }

   [Fact]
   public void Saved_order_is_applied_to_the_movable_folders()
   {
      var folders = Folders("INBOX", "Alpha", "Beta", "Gamma", "Sent");

      var result = FolderOrdering.Apply(folders, ["Gamma", "Alpha", "Beta"]);

      Assert.Equal(["INBOX", "Sent", "Gamma", "Alpha", "Beta"], Names(result));
   }

   [Fact]
   public void Pinned_folders_come_first_in_the_fixed_order_wherever_the_server_lists_them()
   {
      var folders = Folders("Trash", "Work", "Junk", "Sent", "Home", "INBOX");

      var result = FolderOrdering.Apply(folders, ["Work", "Home"]);

      Assert.Equal(["INBOX", "Sent", "Junk", "Trash", "Work", "Home"], Names(result));
   }

   [Fact]
   public void Pinned_names_in_the_saved_order_are_ignored()
   {
      var folders = Folders("INBOX", "Sent", "Alpha", "Beta");

      var result = FolderOrdering.Apply(folders, ["Beta", "Sent", "INBOX", "Alpha"]);

      Assert.Equal(["INBOX", "Sent", "Beta", "Alpha"], Names(result));
   }

   [Fact]
   public void Folders_missing_from_the_saved_order_go_last_alphabetically()
   {
      var folders = Folders("INBOX", "Beta", "Delta", "Alpha", "Charlie");

      var result = FolderOrdering.Apply(folders, ["Beta"]);

      Assert.Equal(["INBOX", "Beta", "Alpha", "Charlie", "Delta"], Names(result));
   }

   [Fact]
   public void Saved_names_that_no_longer_exist_are_ignored()
   {
      var folders = Folders("INBOX", "Alpha", "Beta");

      var result = FolderOrdering.Apply(folders, ["Gone", "Beta", "AlsoGone", "Alpha"]);

      Assert.Equal(["INBOX", "Beta", "Alpha"], Names(result));
   }

   [Fact]
   public void Names_match_case_insensitively()
   {
      var folders = Folders("inbox", "Alpha", "Beta");

      var result = FolderOrdering.Apply(folders, ["BETA", "alpha"]);

      Assert.Equal(["inbox", "Beta", "Alpha"], Names(result));
   }

   [Fact]
   public void A_repeated_saved_name_places_the_folder_only_once()
   {
      var folders = Folders("INBOX", "Alpha", "Beta");

      var result = FolderOrdering.Apply(folders, ["Beta", "Beta", "Alpha"]);

      Assert.Equal(["INBOX", "Beta", "Alpha"], Names(result));
   }

   [Fact]
   public void SortAlphabetical_excludes_pinned_folders_and_ignores_case_and_accents()
   {
      var folders = Folders("Sent", "zebra", "Écoles", "apple", "INBOX", "Banana", "Trash", "Junk");

      var result = FolderOrdering.SortAlphabetical(folders);

      Assert.Equal(["apple", "Banana", "Écoles", "zebra"], result);
   }

   [Theory]
   [InlineData("INBOX", true)]
   [InlineData("Inbox", true)]
   [InlineData("Sent", true)]
   [InlineData("Junk", true)]
   [InlineData("Trash", true)]
   [InlineData("Drafts", false)]
   [InlineData("Archive", false)]
   public void IsPinned_matches_the_four_required_folders(string name, bool expected)
   {
      var folder = F(name);

      Assert.Equal(expected, FolderOrdering.IsPinned(folder, [folder]));
   }

   private static MailFolderInfo Junk(string fullName, string name) =>
      new() { FullName = fullName, Name = name, IsJunk = true };

   [Fact]
   public void The_detected_junk_folder_takes_the_junk_slot_whatever_it_is_called()
   {
      var folders = new List<MailFolderInfo>
      {
         F("Trash"), F("Work"), Junk("[Gmail]/Spam", "Spam"), F("Sent"), F("INBOX"), F("Home"),
      };

      var result = FolderOrdering.Apply(folders, ["Work", "Home"]);

      Assert.Equal(["INBOX", "Sent", "[Gmail]/Spam", "Trash", "Work", "Home"], Names(result));
   }

   [Fact]
   public void A_plain_folder_named_Junk_is_not_pinned_when_another_folder_is_the_detected_junk_folder()
   {
      var folders = new List<MailFolderInfo> { F("INBOX"), F("Junk"), Junk("[Gmail]/Spam", "Spam") };

      var result = FolderOrdering.Apply(folders, ["Junk"]);

      Assert.Equal(["INBOX", "[Gmail]/Spam", "Junk"], Names(result));
      Assert.False(FolderOrdering.IsPinned(folders[1], folders));
      Assert.True(FolderOrdering.IsPinned(folders[2], folders));
   }

   [Fact]
   public void Without_any_flagged_junk_folder_one_named_Junk_still_takes_the_slot()
   {
      var folders = Folders("INBOX", "Work", "Junk");

      var result = FolderOrdering.Apply(folders, ["Work"]);

      Assert.Equal(["INBOX", "Junk", "Work"], Names(result));
   }

   [Fact]
   public void SortAlphabetical_leaves_out_the_detected_junk_folder()
   {
      var folders = new List<MailFolderInfo> { F("INBOX"), F("Zeta"), Junk("[Gmail]/Spam", "Spam"), F("Alpha") };

      Assert.Equal(["Alpha", "Zeta"], FolderOrdering.SortAlphabetical(folders));
   }
}
