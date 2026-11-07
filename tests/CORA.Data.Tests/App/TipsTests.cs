using CORA.App.Services.Tips;

namespace CORA.Data.Tests.App;

public class TipCatalogTests
{
    [Fact]
    public void Has_50_tips_with_unique_ids()
    {
        Assert.Equal(50, TipCatalog.All.Count);
        Assert.Equal(TipCatalog.All.Count, TipCatalog.All.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Every_tip_has_three_languages_and_a_gocora_page()
    {
        foreach (var tip in TipCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tip.En), $"tip {tip.Id} en");
            Assert.False(string.IsNullOrWhiteSpace(tip.Fr), $"tip {tip.Id} fr");
            Assert.False(string.IsNullOrWhiteSpace(tip.Es), $"tip {tip.Id} es");
            Assert.True(tip.Path.StartsWith("help/", StringComparison.Ordinal) || tip.Path.StartsWith("pages/", StringComparison.Ordinal),
                $"tip {tip.Id} path {tip.Path}");
            Assert.DoesNotContain("coracsi", tip.Path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Text_falls_back_to_English()
    {
        var tip = TipCatalog.All[0];
        Assert.Equal(tip.Fr, tip.Text("fr"));
        Assert.Equal(tip.Es, tip.Text("es"));
        Assert.Equal(tip.En, tip.Text("de"));
        Assert.Equal(tip.En, tip.Text(null));
    }

    [Fact]
    public void No_tip_talks_about_removing_ads()
    {
        foreach (var tip in TipCatalog.All)
            Assert.DoesNotContain("remove ads", tip.En, StringComparison.OrdinalIgnoreCase);
    }
}

public class TipRotationTests
{
    private static readonly int[] Ids = Enumerable.Range(1, 10).ToArray();

    [Fact]
    public void A_round_shows_every_tip_once()
    {
        var prefs = new InMemoryPreferenceStore();
        var rotation = new TipRotation(prefs, Ids, new Random(1));
        var shown = Enumerable.Range(0, Ids.Length).Select(_ => rotation.Next()!.Value).ToList();
        Assert.Equal(Ids.OrderBy(i => i), shown.OrderBy(i => i));
    }

    [Fact]
    public void Keeps_its_place_across_launches()
    {
        var prefs = new InMemoryPreferenceStore();
        var first = new TipRotation(prefs, Ids, new Random(2));
        var a = first.Next()!.Value;
        // A new launch: new rotation object, same preferences.
        var shown = new List<int> { a };
        for (var i = 1; i < Ids.Length; i++)
            shown.Add(new TipRotation(prefs, Ids, new Random(i)).Next()!.Value);
        Assert.Equal(Ids.Length, shown.Distinct().Count());
    }

    [Fact]
    public void Never_repeats_a_tip_across_rounds()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var prefs = new InMemoryPreferenceStore();
            var rotation = new TipRotation(prefs, Ids, new Random(seed));
            int? previous = null;
            for (var i = 0; i < Ids.Length * 5; i++)
            {
                var id = rotation.Next()!.Value;
                Assert.NotEqual(previous, id);
                previous = id;
            }
        }
    }

    [Fact]
    public void Retired_tips_are_skipped_and_new_ones_join_the_next_round()
    {
        var prefs = new InMemoryPreferenceStore();
        prefs.Set(TipRotation.QueueKey, "3,99,4");   // 99 was removed in an app update
        var withNew = Ids.Append(11).ToArray();
        var rotation = new TipRotation(prefs, withNew, new Random(3));
        Assert.Equal(3, rotation.Next());
        Assert.Equal(4, rotation.Next());
        var nextRound = Enumerable.Range(0, withNew.Length).Select(_ => rotation.Next()!.Value).ToList();
        Assert.Contains(11, nextRound);
        Assert.DoesNotContain(99, nextRound);
    }

    [Fact]
    public void Bad_or_empty_preferences_start_a_new_round()
    {
        var prefs = new InMemoryPreferenceStore();
        prefs.Set(TipRotation.QueueKey, "x,,y");
        prefs.Set(TipRotation.LastKey, "nope");
        var id = new TipRotation(prefs, Ids, new Random(4)).Next();
        Assert.Contains(id!.Value, Ids);
        Assert.Null(new TipRotation(new InMemoryPreferenceStore(), []).Next());
    }

    [Fact]
    public void One_tip_repeats_itself()
    {
        var rotation = new TipRotation(new InMemoryPreferenceStore(), [7]);
        Assert.Equal(7, rotation.Next());
        Assert.Equal(7, rotation.Next());
    }
}
