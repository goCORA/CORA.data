using CORA.Data.Email;

namespace CORA.Data.Tests.Email;

public class ImapStalePruneTests
{
   private static HashSet<string> Set(params string[] values) => [.. values];

   [Fact]
   public void Rows_missing_from_the_server_are_pruned()
   {
      var stale = MailKitEmailService.FindStaleUidls(["1", "2", "3"], Set("1", "3"), Set());

      Assert.Equal(["2"], stale);
   }

   [Fact]
   public void Rows_still_on_the_server_are_kept()
   {
      var stale = MailKitEmailService.FindStaleUidls(["1", "2"], Set("1", "2", "9"), Set());

      Assert.Empty(stale);
   }

   [Fact]
   public void Locally_moved_rows_are_never_pruned_even_though_the_server_does_not_list_them()
   {
      // "17" was filed here locally (e.g. blacklisted mail moved into "Junk"); the server's
      // list for this folder has no such uid. "2" is a real orphan.
      var stale = MailKitEmailService.FindStaleUidls(["1", "2", "17"], Set("1"), Set("17"));

      Assert.Equal(["2"], stale);
   }

   [Fact]
   public void Nothing_is_pruned_when_every_missing_row_is_local_only()
   {
      var stale = MailKitEmailService.FindStaleUidls(["17", "18"], Set(), Set("17", "18"));

      Assert.Empty(stale);
   }
}
