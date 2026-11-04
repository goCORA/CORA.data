using CORA.Data.Sync;

namespace CORA.Data.Tests.Sync;

public class SyncPhotoPlanTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string C = "cccccccccccccccccccccccccccccccc";

    [Fact]
    public void Uploads_Downloads_AndRemovesWhatNoContactUses()
    {
        var plan = SyncPhotoPlan.Build(
            [$"{A}.jpg", $"{B}.jpg"],
            [$"{A}.jpg"],
            [new SyncCloudPhoto(B, 1), new SyncCloudPhoto(C, 1)],
            1);

        Assert.Equal(new[] { $"{A}.jpg" }, plan.Upload);
        Assert.Equal(new[] { $"{B}.jpg" }, plan.Download);
        Assert.Equal(new[] { C }, plan.Delete);
    }

    [Fact]
    public void NothingToDo_WhenEverythingMatches()
    {
        var plan = SyncPhotoPlan.Build([$"{A}.jpg"], [$"{A}.jpg"], [new SyncCloudPhoto(A, 2)], 2);
        Assert.Empty(plan.Upload);
        Assert.Empty(plan.Download);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void PhotosSealedUnderAnOlderKeyCode_AreSealedAgain_ByADeviceThatHasThem()
    {
        var plan = SyncPhotoPlan.Build([$"{A}.jpg", $"{B}.jpg"], [$"{A}.jpg"], [new SyncCloudPhoto(A, 1), new SyncCloudPhoto(B, 1)], 2);
        Assert.Equal(new[] { $"{A}.jpg" }, plan.Upload);
        Assert.Equal(new[] { $"{B}.jpg" }, plan.Download);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../secrets.txt")]
    [InlineData("photo.jpg")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.jpg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg/..")]
    public void OnlyGoCORAsOwnFileNames_AreAccepted(string name)
    {
        Assert.Null(SyncPhotoPlan.IdOf(name));
        var plan = SyncPhotoPlan.Build([name], [], [new SyncCloudPhoto(A, 1)], 1);
        Assert.Empty(plan.Download);
    }

    [Fact]
    public void ARun_DoesALimitedAmount()
    {
        var names = Enumerable.Range(0, SyncPhotoPlan.MaxPerRun + 5).Select(i => $"{i:x32}.jpg").ToList();
        var plan = SyncPhotoPlan.Build(names, names, [], 1);
        Assert.Equal(SyncPhotoPlan.MaxPerRun, plan.Upload.Count);
    }
}
