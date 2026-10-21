using SkyPass.Core.Tle;
using SkyPass.Core.Time;
using SkyPass.Tests.Support;
using static SkyPass.Tests.Support.TleText;

namespace SkyPass.Tests.Core.Tle;

public class TleCatalogTests
{
    private static readonly TleSet Iss = TleParser.Parse($"ISS\n{IssLine1}\n{IssLine2}").Sets[0];

    private static TleSet IssAt(double dayOfYear, string name = "ISS") => Iss with { EpochDayOfYear = dayOfYear, Name = name };

    [Fact]
    public void Keeps_newest_epoch_as_active_regardless_of_import_order()
    {
        var (catalog, _) = TleCatalog.Empty.Import([IssAt(100), IssAt(120, "ISS NEW"), IssAt(110)]);

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal(120, entry.Active.EpochDayOfYear);
        Assert.Equal("ISS NEW", entry.Name);
        Assert.Equal([120.0, 110.0, 100.0], entry.History.Select(h => h.EpochDayOfYear));
    }

    [Fact]
    public void History_is_capped_at_five_most_recent()
    {
        var (catalog, summary) = TleCatalog.Empty.Import(Enumerable.Range(1, 8).Select(d => IssAt(d * 10)));

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal(TleCatalog.MaxHistory, entry.History.Count);
        Assert.Equal([80.0, 70.0, 60.0, 50.0, 40.0], entry.History.Select(h => h.EpochDayOfYear));
        Assert.Equal(3, summary.DiscardedOlder);
    }

    [Fact]
    public void Older_set_than_full_history_is_discarded_on_later_import()
    {
        var (full, _) = TleCatalog.Empty.Import(Enumerable.Range(1, 5).Select(d => IssAt(d * 10 + 100)));

        var (after, summary) = full.Import([IssAt(5)]);

        Assert.Equal(1, summary.DiscardedOlder);
        Assert.Equal(full.Entries[0].History, after.Entries[0].History);
    }

    [Fact]
    public void Same_epoch_replaces_existing_set()
    {
        var (catalog, _) = TleCatalog.Empty.Import([IssAt(100, "OLD NAME")]);

        var (updated, summary) = catalog.Import([IssAt(100, "NEW NAME")]);

        var entry = Assert.Single(updated.Entries);
        Assert.Single(entry.History);
        Assert.Equal("NEW NAME", entry.Name);
        Assert.Equal(1, summary.Replaced);
        Assert.Equal(0, summary.Added);
    }

    [Fact]
    public void Different_satellites_are_separate_entries_sorted_by_norad()
    {
        var other = Iss with { NoradId = 5, Name = "VANGUARD" };

        var (catalog, summary) = TleCatalog.Empty.Import([IssAt(100), other]);

        Assert.Equal([5, 25544], catalog.Entries.Select(e => e.NoradId));
        Assert.Equal(2, summary.Added);
    }

    [Fact]
    public void Pin_selects_historical_set_and_survives_newer_import()
    {
        var (catalog, _) = TleCatalog.Empty.Import([IssAt(100), IssAt(110)]);
        var pinned = catalog.Pin(25544, IssAt(100).Epoch);

        Assert.Equal(100, pinned.Entries[0].Active.EpochDayOfYear);

        var (afterImport, _) = pinned.Import([IssAt(120)]);
        Assert.Equal(100, afterImport.Entries[0].Active.EpochDayOfYear);
        Assert.Equal(120, afterImport.Entries[0].Latest.EpochDayOfYear);

        var unpinned = afterImport.Unpin(25544);
        Assert.Equal(120, unpinned.Entries[0].Active.EpochDayOfYear);
    }

    [Fact]
    public void Pin_is_cleared_when_pinned_set_falls_out_of_history()
    {
        var (catalog, _) = TleCatalog.Empty.Import(Enumerable.Range(1, 5).Select(d => IssAt(d * 10)));
        var pinned = catalog.Pin(25544, IssAt(10).Epoch);

        var (after, _) = pinned.Import([IssAt(60)]);

        Assert.Null(after.Entries[0].PinnedEpoch);
        Assert.Equal(60, after.Entries[0].Active.EpochDayOfYear);
    }

    [Fact]
    public void Pinning_unknown_set_fails_fast()
    {
        var (catalog, _) = TleCatalog.Empty.Import([IssAt(100)]);

        Assert.Throws<ArgumentException>(() => catalog.Pin(25544, IssAt(101).Epoch));
        Assert.Throws<ArgumentException>(() => catalog.Pin(99999, IssAt(100).Epoch));
    }

    [Fact]
    public void Deep_space_orbit_is_marked_unsupported()
    {
        var geo = Iss with { NoradId = 40000, MeanMotionRevPerDay = 1.00271 };

        var (catalog, summary) = TleCatalog.Empty.Import([IssAt(100), geo]);

        Assert.True(catalog.Find(25544)!.IsSupported);
        Assert.False(catalog.Find(40000)!.IsSupported);
        Assert.Equal(1, summary.Unsupported);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(14.0, false)]
    [InlineData(14.0001, true)]
    [InlineData(-14.0, false)]
    [InlineData(-14.0001, true)]
    public void Stale_when_prediction_time_is_more_than_14_days_from_epoch(double offsetDays, bool expected)
    {
        var (catalog, _) = TleCatalog.Empty.Import([IssAt(100)]);
        var entry = catalog.Entries[0];

        var t = entry.Active.Epoch.AddSeconds(offsetDays * 86400);

        Assert.Equal(expected, entry.IsStaleAt(t));
    }

    [Fact]
    public void Import_does_not_mutate_original_catalog()
    {
        var (first, _) = TleCatalog.Empty.Import([IssAt(100)]);

        _ = first.Import([IssAt(110)]);

        Assert.Single(first.Entries[0].History);
    }

    [Fact]
    public void Property_active_is_newest_and_history_sorted_and_capped()
    {
        Property.Run("目錄：歷史排序、上限與目前組", seed: 7203, iterations: 500, rng =>
        {
            var catalog = TleCatalog.Empty;
            var all = new List<TleSet>();
            for (var batch = 0; batch < rng.Next(1, 5); batch++)
            {
                var sets = Enumerable.Range(0, rng.Next(1, 12))
                    .Select(_ => Iss with { NoradId = rng.Next(1, 4), EpochDayOfYear = rng.Next(1, 40) })
                    .ToList();
                all.AddRange(sets);
                catalog = catalog.Import(sets).Catalog;
            }

            foreach (var entry in catalog.Entries)
            {
                var expected = all.Where(s => s.NoradId == entry.NoradId)
                    .Select(s => s.EpochDayOfYear).Distinct().OrderDescending().Take(TleCatalog.MaxHistory);
                Assert.Equal(expected, entry.History.Select(h => h.EpochDayOfYear));
                Assert.Equal(entry.History[0], entry.Active);
            }
        });
    }
}
