using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;
using V2rayNMonitor.Core;
using Xunit;

namespace V2rayNMonitor.Core.Tests;

public sealed class LedgerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "v2rayn-ledger-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2024-02-28T23:59:00Z", CultureInfo.InvariantCulture);
    private string Database => Path.Combine(directory, "synthetic.db");
    private static CounterSample Sample(double seconds, long up, long down = 0, string instance = "instance", string node = "node", string session = "process-1", string generation = "config-1", string name = "Synthetic node") =>
        new(instance, node, name, session, generation, Epoch.AddSeconds(seconds), up, down);
    private static QuerySpec All(string bucket = "day", string? node = null, int page = 0, int size = 50) =>
        new(Epoch.AddYears(-2), Epoch.AddYears(3), "UTC", bucket, node, page, size);

    [Fact]
    public void FirstReadIsBaselineAndReplayOrOlderSamplesDoNotAddBytes()
    {
        using var ledger = new Ledger(Database);
        Assert.Equal("baseline", ledger.Accept(Sample(0, 500_000, 900_000)));
        Assert.Equal("0", ledger.Query(All()).Upload);
        var accepted = Sample(1, 500_023, 900_077);
        Assert.Equal("recorded", ledger.Accept(accepted));
        Assert.Equal("duplicate", ledger.Accept(accepted));
        Assert.Equal("stale", ledger.Accept(Sample(0.5, 999_999, 999_999)));
        var result = ledger.Query(All());
        Assert.Equal("23", result.Upload);
        Assert.Equal("77", result.Download);
        Assert.Single(result.Records);
        Assert.Equal(0, result.GapCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetOfEitherDirectionEstablishesANewBaseline(bool resetUpload)
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 100, 100));
        Assert.Equal("reset", ledger.Accept(Sample(1, resetUpload ? 90 : 120, resetUpload ? 120 : 90)));
        ledger.Accept(Sample(2, resetUpload ? 95 : 125, resetUpload ? 125 : 95));
        var result = ledger.Query(All());
        Assert.Equal("5", result.Upload);
        Assert.Equal("5", result.Download);
        Assert.Equal(1, result.GapCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedProcessOrGenerationWithHigherCountersCannotCreateASpike(bool changeProcess)
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 0));
        ledger.Accept(Sample(1, 100));
        var session = changeProcess ? "process-2" : "process-1";
        var generation = changeProcess ? "config-1" : "config-2";
        Assert.Equal("baseline", ledger.Accept(Sample(2, 8_000_000, session: session, generation: generation)));
        ledger.Accept(Sample(3, 8_000_020, session: session, generation: generation));
        Assert.Equal("120", ledger.Query(All()).Upload);
        Assert.Equal(1, ledger.Query(All()).GapCount);
    }

    [Fact]
    public void SwitchingNodesAndBackNeverReusesAnOldNodeCursor()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 0, node: "a"));
        ledger.Accept(Sample(1, 10, node: "a"));
        ledger.Accept(Sample(2, 1000, node: "b"));
        ledger.Accept(Sample(3, 1020, node: "b"));
        ledger.Accept(Sample(4, 4000, node: "a"));
        ledger.Accept(Sample(5, 4003, node: "a"));
        var result = ledger.Query(All());
        Assert.Equal("33", result.Upload);
        Assert.Equal("13", result.Nodes.Single(x => x.Key == "instance/a").Upload);
        Assert.Equal("20", result.Nodes.Single(x => x.Key == "instance/b").Upload);
        Assert.Equal(2, result.GapCount);
    }

    [Fact]
    public void RenamePreservesHistoryAndKeysDoNotMergeOnNamesOrSeparators()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 0, instance: "a/b", node: "c"));
        ledger.Accept(Sample(1, 20, instance: "a/b", node: "c", name: "Renamed synthetic"));
        ledger.Accept(Sample(0, 0, instance: "a", node: "b/c"));
        ledger.Accept(Sample(1, 30, instance: "a", node: "b/c", name: "Renamed synthetic"));
        Assert.Equal(2, ledger.GetNodes().Count);
        var result = ledger.Query(All());
        Assert.Equal("50", result.Upload);
        Assert.Equal(2, result.Nodes.Count);
        Assert.All(result.Records, x => Assert.Equal("Renamed synthetic", x.NodeName));
        Assert.Contains(result.Nodes, x => x.Key == "a%2Fb/c" && x.Upload == "20");
        Assert.Contains(result.Nodes, x => x.Key == "a/b%2Fc" && x.Upload == "30");
    }

    [Fact]
    public void ShortMidnightIntervalConservesIntegerBytesAcrossLeapDay()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(58, 0, 0));
        ledger.Accept(Sample(62, 7, 11));
        var before = ledger.Query(new QuerySpec(new DateTimeOffset(Epoch.Date, TimeSpan.Zero), new DateTimeOffset(Epoch.Date.AddDays(1), TimeSpan.Zero), "UTC"));
        var after = ledger.Query(new QuerySpec(new DateTimeOffset(Epoch.Date.AddDays(1), TimeSpan.Zero), new DateTimeOffset(Epoch.Date.AddDays(2), TimeSpan.Zero), "UTC"));
        Assert.Equal("3", before.Upload);
        Assert.Equal("5", before.Download);
        Assert.Equal("4", after.Upload);
        Assert.Equal("6", after.Download);
        Assert.Equal("2024-02-29", Assert.Single(after.Series).Label);
        Assert.All(ledger.Query(All()).Records, x => Assert.Equal("approximate", x.Quality));
        Assert.Equal("7", ledger.Query(All()).Upload);
    }

    [Fact]
    public void LocalMidnightAndMondayMonthYearBucketsUseTheRequestedTimeZone()
    {
        using var ledger = new Ledger(Database);
        var localBoundary = DateTimeOffset.Parse("2023-12-31T16:00:00Z", CultureInfo.InvariantCulture);
        ledger.Accept(Sample(0, 0) with { At = localBoundary.AddSeconds(-2) });
        ledger.Accept(Sample(0, 8) with { At = localBoundary.AddSeconds(2) });
        var query = new QuerySpec(localBoundary.AddDays(-1), localBoundary.AddDays(1), "Asia/Shanghai");
        Assert.Equal(new[] { "2023-12-31", "2024-01-01" }, ledger.Query(query).Series.Select(x => x.Label));
        Assert.Equal(new[] { "2023-12-25", "2024-01-01" }, ledger.Query(query with { Bucket = "week" }).Series.Select(x => x.Label));
        Assert.Equal(new[] { "2023-12", "2024-01" }, ledger.Query(query with { Bucket = "month" }).Series.Select(x => x.Label));
        Assert.Equal(new[] { "2023", "2024" }, ledger.Query(query with { Bucket = "year" }).Series.Select(x => x.Label));
        Assert.Equal("8", ledger.Query(query).Upload);
    }

    [Theory]
    [InlineData("2024-11-03T05:30:00Z", 60, "America/New_York", "2024-11-03T01:00-04:00", "2024-11-03T01:00-05:00")]
    [InlineData("2024-04-06T14:40:00Z", 30, "Australia/Lord_Howe", "2024-04-07T01:00+11:00", "2024-04-07T01:00+10:30")]
    public void RepeatedDaylightSavingHourKeepsItsTwoOffsetsInChronologicalOrder(string at, int stepMinutes, string zone, string firstLabel, string secondLabel)
    {
        using var ledger = new Ledger(Database);
        var first = DateTimeOffset.Parse(at, CultureInfo.InvariantCulture);
        for (var i = 0; i < 2; i++)
        {
            ledger.Accept(Sample(0, 0, instance: $"synthetic-{i}") with { At = first.AddMinutes(i * stepMinutes) });
            ledger.Accept(Sample(0, 5, instance: $"synthetic-{i}") with { At = first.AddMinutes(i * stepMinutes).AddSeconds(1) });
        }
        var result = ledger.Query(new QuerySpec(first.AddHours(-1), first.AddHours(2), zone, "hour"));
        Assert.Equal(new[] { firstLabel, secondLabel }, result.Series.Select(x => x.Label));
        Assert.Equal("10", result.Upload);
    }

    [Fact]
    public void RecoveredIntervalInsideOneHourContributesToKnownTotalsAndNodeRanking()
    {
        using var ledger = new Ledger(Database);
        var start = DateTimeOffset.Parse("2026-01-15T10:00:00Z", CultureInfo.InvariantCulture);
        ledger.Accept(Sample(0, 0, 0) with { At = start });
        ledger.Accept(Sample(0, 1000, 2000) with { At = start.AddSeconds(1) });
        // A new session begins with a baseline; only its subsequent continuous delta is recoverable.
        ledger.Accept(Sample(0, 0, 0, session: "process-2") with { At = start.AddMinutes(15) });
        ledger.Accept(Sample(0, 1200, 2800, session: "process-2") with { At = start.AddMinutes(45) });

        var dayStart = new DateTimeOffset(start.Date, TimeSpan.Zero);
        var day = new QuerySpec(dayStart, dayStart.AddDays(1), "UTC", "hour");
        var result = ledger.Query(day);
        Assert.Equal("2200", result.Upload);
        Assert.Equal("4800", result.Download);
        Assert.Equal("2200", Assert.Single(result.Nodes).Upload);
        Assert.Equal("4800", Assert.Single(result.Nodes).Download);
        Assert.Equal("1200", Assert.Single(result.Nodes).RecoveredUpload);
        Assert.Equal("2800", Assert.Single(result.Nodes).RecoveredDownload);
        Assert.Equal("2200", Assert.Single(result.Series).Upload);
        Assert.Equal("4800", Assert.Single(result.Series).Download);
        Assert.Equal("1200", Assert.Single(result.Series).RecoveredUpload);
        Assert.Equal("2800", Assert.Single(result.Series).RecoveredDownload);

        // Knowing the whole interval's total does not establish how much occurred in this subrange.
        var partial = ledger.Query(day with { Start = start.AddMinutes(20), End = start.AddMinutes(30) });
        Assert.Equal("0", partial.Upload);
        Assert.Equal("0", partial.Download);
        Assert.Empty(partial.Nodes);
        Assert.Empty(partial.Series);
    }

    [Theory]
    [InlineData("hour", "2026-01-02T00:00+08:00", "25")]
    [InlineData("week", "2025-12-29", "32")]
    [InlineData("month", "2026-01", "32")]
    public void SameNameFilterIncludesRecoveryAndPreservesSelectedTimeZoneBuckets(string bucket, string label, string firstAmount)
    {
        using var ledger = new Ledger(Database);
        var start = DateTimeOffset.Parse("2026-01-01T15:15:00Z", CultureInfo.InvariantCulture);
        ledger.Accept(Sample(0, 0, name: "Same") with { At = start });
        ledger.Accept(Sample(0, 25, name: "Same") with { At = start.AddHours(1) });
        ledger.Accept(Sample(0, 0, name: "same", instance: "other") with { At = start });
        ledger.Accept(Sample(0, 100, name: "same", instance: "other") with { At = start.AddSeconds(1) });
        ledger.Accept(Sample(0, 32, name: "Same") with { At = start.AddHours(1).AddSeconds(1) });
        var result = ledger.Query(new QuerySpec(start.AddDays(-1), start.AddDays(2), "Asia/Shanghai", bucket) { NodeName = "Same" });
        Assert.Equal("32", result.Upload); Assert.Single(result.Nodes);
        Assert.Equal("25", result.RecoveredUpload); Assert.Equal(2, result.RecordCount);
        // Recovery crosses midnight/hour: hour excludes it, while week/month include it in one bucket.
        Assert.Equal(bucket == "hour" ? "25" : "0", result.UnbucketedUpload);
        Assert.Equal(label, Assert.Single(result.Series).Label);
        Assert.Equal(bucket == "hour" ? "7" : firstAmount, result.Series[0].Upload);
        Assert.Equal("0", ledger.Query(new QuerySpec(start.AddMinutes(20), start.AddMinutes(30), "Asia/Shanghai") { NodeName = "Same" }).Upload);
    }

    [Theory]
    [InlineData("2026-01-15T10:30:00Z", "2026-01-15T11:00:00Z", "UTC", "hour", true)]
    [InlineData("2026-01-15T10:30:00Z", "2026-01-15T11:01:00Z", "UTC", "hour", false)]
    [InlineData("2026-01-15T15:30:00Z", "2026-01-15T16:00:00Z", "Asia/Shanghai", "day", true)]
    [InlineData("2026-01-15T15:30:00Z", "2026-01-15T16:01:00Z", "Asia/Shanghai", "day", false)]
    [InlineData("2024-11-03T05:30:00Z", "2024-11-03T06:30:00Z", "America/New_York", "hour", false)]
    public void RecoveryUsesHalfOpenBucketsAndTimeZoneWithoutSplitting(string from, string to, string zone, string bucket, bool fits)
    {
        using var ledger = new Ledger(Database);
        var start = DateTimeOffset.Parse(from, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(to, CultureInfo.InvariantCulture);
        ledger.Accept(Sample(0, 0) with { At = start });
        ledger.Accept(Sample(0, 123) with { At = end });
        var query = new QuerySpec(start, end, zone, bucket, "instance/node");
        var result = ledger.Query(query);
        Assert.Equal("123", result.Upload);
        Assert.Equal("123", result.RecoveredUpload);
        Assert.Equal(fits ? "0" : "123", result.UnbucketedUpload);
        Assert.Equal(fits ? 1 : 0, result.Series.Count);
        Assert.Equal("123", Assert.Single(result.Nodes).Upload);
        Assert.Equal("0", ledger.Query(query with { Start = end, End = end.AddHours(1) }).Upload);
        Assert.Equal("0", ledger.Query(query with { NodeKey = "other" }).Upload);
    }

    [Fact]
    public void LongContinuousIntervalIsRecoveredButNotAssignedToDates()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 100, 200));
        Assert.Equal("unallocated", ledger.Accept(Sample(7200, 125, 280)));
        var result = ledger.Query(All());
        Assert.Equal("25", result.Upload);
        Assert.Equal("80", result.Download);
        Assert.Equal("25", result.UnallocatedUpload);
        Assert.Equal("80", result.UnallocatedDownload);
        Assert.Empty(result.Series);
        Assert.Equal("25", Assert.Single(result.Nodes).Upload);
        Assert.Equal("25", result.UnbucketedUpload);
        Assert.Equal("recovered-contained", Assert.Single(result.Records).Quality);
        Assert.Equal(1, result.GapCount);
        // An intersecting range reports the whole uncertain interval, never a made-up proportional part.
        var overlap = ledger.Query(new QuerySpec(Epoch.AddMinutes(30), Epoch.AddMinutes(40), "UTC"));
        Assert.Equal("25", overlap.UnallocatedUpload);
        Assert.Equal("25", overlap.PartialUpload);
        Assert.Equal("0", overlap.Upload);
        Assert.Equal("recovered-partial", Assert.Single(overlap.Records).Quality);
        var outside = ledger.Query(new QuerySpec(Epoch.AddHours(2), Epoch.AddHours(3), "UTC"));
        Assert.Equal("0", outside.UnallocatedUpload);
    }

    [Fact]
    public void ExplicitOutageIsCoalescedAndEvenShortRecoveryIsUnallocated()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 100));
        ledger.RecordGap(Epoch.AddSeconds(1), "network_unavailable");
        ledger.RecordGap(Epoch.AddSeconds(2), "example endpoint and secret must not be persisted");
        Assert.Equal(1, ledger.Query(All()).GapCount);
        Assert.Equal("unallocated", ledger.Accept(Sample(3, 130)));
        ledger.Accept(Sample(4, 137));
        var result = ledger.Query(All());
        Assert.Equal("37", result.Upload);
        Assert.Equal("30", result.UnallocatedUpload);
        Assert.Equal("30", result.RecoveredUpload);
        Assert.False(Assert.Single(result.Gaps).IsOpen);
        Assert.Equal(1, result.GapCount);
        Assert.Equal(2, result.RecordCount);
    }

    [Fact]
    public void OutageFollowedByChangedGenerationCannotRecoverUnknownBytes()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 100));
        ledger.RecordGap(Epoch.AddSeconds(1), "mapping_unavailable");
        Assert.Equal("baseline", ledger.Accept(Sample(2, 500, generation: "config-2")));
        var result = ledger.Query(All());
        Assert.Equal("0", result.Upload);
        Assert.Equal("0", result.UnallocatedUpload);
        Assert.Equal(1, result.GapCount);
    }

    [Fact]
    public void DecimalStringsRemainExactBeyondJavaScriptAndInt64AggregateLimits()
    {
        using var ledger = new Ledger(Database);
        for (var i = 0; i < 2; i++)
        {
            ledger.Accept(Sample(0, 0, instance: $"synthetic-{i}"));
            ledger.Accept(Sample(1, long.MaxValue, 9_007_199_254_740_993, instance: $"synthetic-{i}"));
        }
        var result = ledger.Query(All());
        Assert.Equal("18446744073709551614", result.Upload);
        Assert.Equal("18014398509481986", result.Download);
        Assert.All(result.Records, x => Assert.Equal("9223372036854775807", x.Upload));
        Assert.Equal(result.Upload, Assert.Single(result.Series).Upload);
        Assert.Equal(BigInteger.Parse(result.Upload, CultureInfo.InvariantCulture), result.Nodes.Aggregate(BigInteger.Zero, (sum, node) => sum + BigInteger.Parse(node.Upload, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void FailedCursorWriteRollsBackMinuteAndCanBeRetriedExactlyOnce()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 0));
        RunSql("CREATE TRIGGER synthetic_failure BEFORE INSERT ON cursors WHEN NEW.upload=20 BEGIN SELECT RAISE(ABORT,'synthetic write failure'); END;");
        Assert.Throws<SqliteException>(() => ledger.Accept(Sample(1, 20)));
        Assert.Equal("0", ledger.Query(All()).Upload);
        Assert.Empty(ledger.Query(All()).Records);
        RunSql("DROP TRIGGER synthetic_failure;");
        Assert.Equal("recorded", ledger.Accept(Sample(1, 20)));
        Assert.Equal("20", ledger.Query(All()).Upload);
        Assert.Equal("duplicate", ledger.Accept(Sample(1, 20)));
    }

    [Fact]
    public void PerMinuteOverflowThrowsInsteadOfSilentlyStoringFloatingPoint()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 0));
        ledger.Accept(Sample(1, long.MaxValue));
        ledger.Accept(Sample(2, 0));
        Assert.Throws<OverflowException>(() => ledger.Accept(Sample(3, 1)));
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), ledger.Query(All()).Upload);
        RunSql("UPDATE minutes SET upload=0;");
        Assert.Equal("recorded", ledger.Accept(Sample(3, 1)));
        Assert.Equal("1", ledger.Query(All()).Upload);
    }

    [Fact]
    public void ReopeningPreservesBothCommittedCursorAndPendingOutage()
    {
        using (var ledger = new Ledger(Database))
        {
            ledger.Accept(Sample(0, 100));
            ledger.Accept(Sample(1, 120));
        }
        using (var ledger = new Ledger(Database))
        {
            Assert.Equal("duplicate", ledger.Accept(Sample(1, 120)));
            ledger.Accept(Sample(2, 130));
            ledger.RecordGap(Epoch.AddSeconds(3), "network_unavailable");
        }
        using (var ledger = new Ledger(Database))
        {
            Assert.Equal("unallocated", ledger.Accept(Sample(4, 140)));
            var result = ledger.Query(All());
            Assert.Equal("40", result.Upload);
            Assert.Equal("10", result.UnallocatedUpload);
            Assert.Equal(1, result.GapCount);
        }
    }

    [Fact]
    public void NodeAndHalfOpenMinuteFiltersUseTheSamePagedRecordTotals()
    {
        using var ledger = new Ledger(Database);
        for (var minute = 0; minute < 3; minute++)
            for (var node = 0; node < 2; node++)
            {
                var sample = Sample(minute * 60 + node * 2, 0, node: $"node-{node}", session: $"process-{minute}");
                ledger.Accept(sample);
                ledger.Accept(sample with { At = sample.At.AddSeconds(1), Upload = 10 + node });
            }
        var result = ledger.Query(new QuerySpec(Epoch, Epoch.AddMinutes(2), "UTC", NodeKey: "instance/node-1", PageSize: 1));
        Assert.Equal("22", result.Upload);
        Assert.Equal(2, result.RecordCount);
        Assert.Equal("11", Assert.Single(result.Records).Upload);
        Assert.Equal(Epoch.AddMinutes(1), DateTimeOffset.Parse(result.Records[0].Start, CultureInfo.InvariantCulture));
        var second = ledger.Query(new QuerySpec(Epoch, Epoch.AddMinutes(2), "UTC", NodeKey: "instance/node-1", Page: 1, PageSize: 1));
        Assert.Equal(result.Upload, second.Upload);
        Assert.Equal(Epoch, DateTimeOffset.Parse(Assert.Single(second.Records).Start, CultureInfo.InvariantCulture));
        Assert.Empty(ledger.Query(All(node: "missing")).Records);
    }

    [Fact]
    public void IdleSamplesAreMeasuredZeroAndNotMissingData()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 100));
        ledger.Accept(Sample(1, 100));
        var result = ledger.Query(All());
        Assert.Equal("0", result.Upload);
        Assert.Equal(0, result.GapCount);
        Assert.Equal("measured", Assert.Single(result.Records).Quality);
    }

    [Fact]
    public void ConcurrentReplaysRemainIdempotent()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 0));
        Parallel.For(0, 20, _ => ledger.Accept(Sample(1, 10)));
        Assert.Equal("10", ledger.Query(All()).Upload);
        Assert.Single(ledger.Query(All()).Records);
    }

    [Fact]
    public void DemoHasFourHundredDaysAndThreeSyntheticNodesAndNeverOverwritesData()
    {
        using var ledger = new Ledger(Database);
        ledger.SeedDemo(Epoch);
        var first = ledger.Query(All(size: 1000));
        Assert.Equal(3, ledger.GetNodes().Count);
        Assert.Equal(1200, first.RecordCount);
        Assert.All(first.Records, x => Assert.Equal("synthetic", x.Quality));
        ledger.SeedDemo(Epoch.AddDays(1));
        Assert.Equal(first.Upload, ledger.Query(All()).Upload);
        Assert.Equal(1200, ledger.Query(All()).RecordCount);
    }

    [Fact]
    public void RejectsInvalidInputsWithoutChangingTheLedger()
    {
        using var ledger = new Ledger(Database);
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Accept(Sample(0, -1)));
        Assert.Throws<ArgumentException>(() => ledger.Query(All() with { End = All().Start }));
        Assert.Throws<ArgumentException>(() => ledger.Query(All() with { Bucket = "fortnight" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Query(All() with { Page = -1 }));
        Assert.Empty(ledger.GetNodes());
    }

    private void RunSql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
