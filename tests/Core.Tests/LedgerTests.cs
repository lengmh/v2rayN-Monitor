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

    [Fact]
    public void PartialCoveragePersistsWithoutInterruptingTheVerifiedCounter()
    {
        using (var ledger = new Ledger(Database))
        {
            Assert.Equal("baseline", ledger.Accept(Sample(0, 100, 200) with { PartialCoverage = true }));
            Assert.Equal("recorded", ledger.Accept(Sample(1, 110, 230) with { PartialCoverage = true }));
            Assert.Equal("recorded", ledger.Accept(Sample(2, 120, 240) with { PartialCoverage = true }));
            Assert.Equal("duplicate", ledger.Accept(Sample(2, 120, 240) with { PartialCoverage = true }));
        }
        using var reopened = new Ledger(Database);
        Assert.Equal("recorded", reopened.Accept(Sample(3, 130, 250) with { PartialCoverage = true }));
        var result = reopened.Query(All());
        Assert.Equal("30", result.Upload);
        Assert.Equal("50", result.Download);
        Assert.Equal("0", result.UnallocatedUpload);
        Assert.True(result.PartialCoverage);
        var coverage = Assert.Single(result.Gaps);
        Assert.Equal("uncollected_outbounds", coverage.Reason);
        Assert.False(coverage.IsOpen);
        Assert.Equal(Epoch, DateTimeOffset.Parse(coverage.Start, CultureInfo.InvariantCulture));
        Assert.Equal(Epoch.AddSeconds(3), DateTimeOffset.Parse(coverage.End, CultureInfo.InvariantCulture));
        Assert.All(result.Records, row => Assert.True(row.PartialCoverage));
        Assert.All(reopened.Export(All()), row => Assert.True(row.PartialCoverage));
        Assert.Empty(reopened.Query(All(node: "instance/other")).Gaps);
        Assert.False(reopened.Query(All(node: "instance/other")).PartialCoverage);
    }

    [Fact]
    public void PartialCoverageFailureRollsBackBytesAndCursorTogether()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 100) with { PartialCoverage = true });
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False"); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_coverage BEFORE UPDATE ON gaps
            WHEN OLD.reason='uncollected_outbounds' BEGIN SELECT RAISE(ABORT,'Synthetic failure'); END;
            """;
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => ledger.Accept(Sample(1, 150) with { PartialCoverage = true }));
        Assert.Equal("0", ledger.Query(All()).Upload);
        command.CommandText = "SELECT upload FROM cursors"; Assert.Equal(100L, command.ExecuteScalar());
        command.CommandText = "DROP TRIGGER fail_coverage"; command.ExecuteNonQuery();
        Assert.Equal("recorded", ledger.Accept(Sample(2, 170) with { PartialCoverage = true }));
        Assert.Equal("70", ledger.Query(All()).Upload);
        Assert.Single(ledger.Query(All()).Gaps);
    }

    [Fact]
    public void PartialCoverageRecoveryRetainsItsOwnUnknownScopeAndThenEndsAtANewGeneration()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 100) with { PartialCoverage = true });
        ledger.Accept(Sample(1, 110) with { PartialCoverage = true });
        ledger.RecordGap(Epoch.AddSeconds(2), "interface_unavailable");
        Assert.Equal("unallocated", ledger.Accept(Sample(20, 200) with { PartialCoverage = true }));
        Assert.True(Assert.Single(ledger.Query(All()).Records, row => row.Quality == "recovered-contained").PartialCoverage);
        Assert.Equal("baseline", ledger.Accept(Sample(61, 1000, generation: "config-2")));
        Assert.Equal("recorded", ledger.Accept(Sample(62, 1020, generation: "config-2")));
        var result = ledger.Query(All());
        Assert.Equal("120", result.Upload);
        Assert.Contains(result.Records, row => !row.PartialCoverage);
        var coverage = Assert.Single(result.Gaps, gap => gap.Reason == "uncollected_outbounds");
        Assert.Equal(Epoch.AddSeconds(20), DateTimeOffset.Parse(coverage.End, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PartialCoverageSummarySurvivesTheHistoricalGapDisplayLimit()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 100) with { PartialCoverage = true });
        ledger.Accept(Sample(1, 110) with { PartialCoverage = true });
        for (var i = 1; i <= 201; i++)
        {
            ledger.Accept(Sample(i * 60 + 1, 1000, session: $"synthetic-process-{i}"));
            ledger.Accept(Sample(i * 60 + 2, 1001, session: $"synthetic-process-{i}"));
        }
        var result = ledger.Query(All(page: 0, size: 1));
        Assert.DoesNotContain(result.Gaps, gap => gap.Reason == "uncollected_outbounds");
        Assert.False(Assert.Single(result.Records).PartialCoverage);
        Assert.True(result.PartialCoverage);
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
        ledger.RecordGap(Epoch.AddSeconds(-1), "network_unavailable");
        Assert.Equal(1, ledger.Query(All()).GapCount);
        var openGap = Assert.Single(ledger.Query(All()).Gaps);
        Assert.Equal(Epoch, DateTimeOffset.Parse(openGap.Start, CultureInfo.InvariantCulture));
        Assert.Equal(Epoch.AddSeconds(2), DateTimeOffset.Parse(openGap.End, CultureInfo.InvariantCulture));
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
    public void OutageStartsAtLatestCursorRatherThanAnUnrelatedHistoricalInstance()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 100, instance: "old-instance") with { At = Epoch.AddMonths(-2) });
        ledger.Accept(Sample(0, 200));
        ledger.Accept(Sample(1, 210));

        ledger.RecordGap(Epoch.AddSeconds(2), "network_unavailable");

        var gap = Assert.Single(ledger.Query(All()).Gaps);
        Assert.Equal(Epoch.AddSeconds(1), DateTimeOffset.Parse(gap.Start, CultureInfo.InvariantCulture));
        Assert.Equal(Epoch.AddSeconds(2), DateTimeOffset.Parse(gap.End, CultureInfo.InvariantCulture));
        Assert.True(gap.IsOpen);
        Assert.Equal(0, ledger.Query(new QuerySpec(Epoch.AddDays(-1), Epoch, "UTC")).GapCount);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutageWithoutAnEarlierCursorStartsAtFailureTime(bool hasFutureCursor)
    {
        using var ledger = new Ledger(Database);
        if (hasFutureCursor) ledger.Accept(Sample(2, 100));

        ledger.RecordGap(Epoch.AddSeconds(1), "network_unavailable");

        var gap = Assert.Single(ledger.Query(All()).Gaps);
        Assert.Equal(Epoch.AddSeconds(1), DateTimeOffset.Parse(gap.Start, CultureInfo.InvariantCulture));
        Assert.Equal(gap.Start, gap.End);
        Assert.True(gap.IsOpen);
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
            ledger.Accept(Sample(0, 100, instance: "old-instance") with { At = Epoch.AddMonths(-2) });
            ledger.Accept(Sample(0, 100));
            ledger.Accept(Sample(1, 120));
        }
        using (var ledger = new Ledger(Database))
        {
            Assert.Equal("duplicate", ledger.Accept(Sample(1, 120)));
            ledger.Accept(Sample(2, 130));
            ledger.RecordGap(Epoch.AddSeconds(3), "network_unavailable");
            Assert.Equal(Epoch.AddSeconds(2), DateTimeOffset.Parse(Assert.Single(ledger.Query(All()).Gaps).Start, CultureInfo.InvariantCulture));
        }
        using (var ledger = new Ledger(Database))
        {
            ledger.RecordGap(Epoch.AddSeconds(3.5), "network_unavailable");
            var pending = Assert.Single(ledger.Query(All()).Gaps);
            Assert.Equal(Epoch.AddSeconds(2), DateTimeOffset.Parse(pending.Start, CultureInfo.InvariantCulture));
            Assert.Equal(Epoch.AddSeconds(3.5), DateTimeOffset.Parse(pending.End, CultureInfo.InvariantCulture));
            Assert.True(pending.IsOpen);
            Assert.Equal("unallocated", ledger.Accept(Sample(4, 140)));
            var result = ledger.Query(All());
            Assert.Equal("40", result.Upload);
            Assert.Equal("10", result.UnallocatedUpload);
            Assert.Equal(1, result.GapCount);
            Assert.Equal(Epoch.AddSeconds(2), DateTimeOffset.Parse(Assert.Single(result.Gaps).Start, CultureInfo.InvariantCulture));
            Assert.Equal(Epoch.AddSeconds(4), DateTimeOffset.Parse(result.Gaps[0].End, CultureInfo.InvariantCulture));
            Assert.False(result.Gaps[0].IsOpen);
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
    public void ExportRetainsOneCompleteSnapshotWhileTheCollectorContinues()
    {
        using var ledger = new Ledger(Database);
        for (var minute = 0; minute < 1000; minute++)
        {
            ledger.Accept(Sample(minute * 60, 0, session: $"process-{minute}"));
            ledger.Accept(Sample(minute * 60 + 1, minute + 1, session: $"process-{minute}"));
        }
        var query = All(page: 9, size: 1);
        var exported = ledger.Export(query);
        // Export returns the full snapshot before CSV formatting, without deferred page reads.
        var rows = exported.Take(500).ToList();
        ledger.Accept(Sample(60_000, 0, session: "process-1000", name: "Renamed synthetic"));
        ledger.Accept(Sample(60_001, 1001, session: "process-1000", name: "Renamed synthetic"));
        rows.AddRange(exported.Skip(500));

        Assert.Equal(1000, rows.Count);
        Assert.Equal(1000, rows.Select(row => (row.Start, row.NodeKey)).Distinct().Count());
        Assert.Equal(500_500, rows.Sum(row => long.Parse(row.Upload, CultureInfo.InvariantCulture)));
        Assert.Equal(Epoch.ToString("O"), rows[^1].Start);
        Assert.All(rows, row => Assert.Equal("Synthetic node", row.NodeName));
        var next = ledger.Export(query);
        Assert.Equal(1001, next.Count);
        Assert.Equal(501_501, next.Sum(row => long.Parse(row.Upload, CultureInfo.InvariantCulture)));
        Assert.All(next, row => Assert.Equal("Renamed synthetic", row.NodeName));
    }

    [Fact]
    public void ExportSharesExactFiltersOrderingAndRecoveryLabelsWithHistory()
    {
        using var ledger = new Ledger(Database);
        foreach (var (node, name) in new[] { ("a", "Same"), ("b", "Same"), ("c", "same"), ("d", "Same ") })
        {
            ledger.Accept(Sample(0, 0, instance: node, name: name));
            ledger.Accept(Sample(1, 9_007_199_254_740_993, 7, instance: node, name: name));
            ledger.Accept(Sample(120, 9_007_199_254_741_093, 27, instance: node, name: name));
            ledger.Accept(Sample(121, 9_007_199_254_741_096, 29, instance: node, name: name));
        }
        var query = new QuerySpec(Epoch, Epoch.AddMinutes(2), "UTC", PageSize: 1000);
        foreach (var filter in new[] { query, query with { NodeName = "Same" }, query with { NodeKey = "a/node" },
            query with { NodeKey = "a/node", NodeName = "same" }, query with { Start = Epoch.AddSeconds(30) } })
            Assert.Equal(ledger.Query(filter).Records, ledger.Export(filter));
        var contained = ledger.Export(query with { NodeKey = "a/node" });
        Assert.Equal(2, contained.Count);
        Assert.Equal("recovered-contained", contained[0].Quality);
        Assert.Equal("9007199254740993", contained[1].Upload);
        var partial = ledger.Export(query with { Start = Epoch.AddSeconds(30), NodeKey = "a/node" });
        Assert.Equal("recovered-partial", Assert.Single(partial).Quality);
        Assert.Equal("100", partial[0].Upload);
    }

    [Fact]
    public void ExportAllowsOneHundredThousandRowsAndRejectsOverflowIncludingRecovery()
    {
        using var ledger = new Ledger(Database);
        ledger.Accept(Sample(0, 0));
        RunSql($"""
            WITH RECURSIVE synthetic(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM synthetic WHERE n<99998)
            INSERT INTO minutes(start_ticks,node_key,upload,download)
            SELECT {Epoch.UtcTicks}+n*{TimeSpan.TicksPerMinute},'instance/node',1,2 FROM synthetic;
            INSERT INTO unallocated(start_ticks,end_ticks,node_key,upload,download,quality)
            VALUES({Epoch.UtcTicks},{Epoch.AddMinutes(2).UtcTicks},'instance/node',3,4,'recovered');
            """);
        Assert.Equal(100_000, ledger.Export(All()).Count);
        RunSql($"""
            INSERT INTO unallocated(start_ticks,end_ticks,node_key,upload,download,quality)
            VALUES({Epoch.UtcTicks},{Epoch.AddMinutes(3).UtcTicks},'instance/node',5,6,'recovered');
            """);
        Assert.Contains("100000", Assert.Throws<ArgumentException>(() => ledger.Export(All())).Message);
        Assert.Empty(ledger.Export(All(node: "missing")));
        Assert.Single(ledger.Export(All() with { Start = Epoch.AddMinutes(99_998) }));
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

    // All v2 identities and counters below are synthetic. No real client or history file is opened.
    private static MeasurementScope Member(string id = "a", string name = "Synthetic member") =>
        new("actual_member", "index_id", id, name, id, "fixture-" + id);
    private static ScopeReading Reading(string id, long up, long down = 0) => new(Member(id), up, down);
    private static ScopeBatch Batch(double seconds, params ScopeReading[] readings) =>
        new("instance", "process-1", "config-1", Epoch.AddSeconds(seconds), readings, []);
    private static ScopeQuery ScopeAll(string basis = "actual_member", long? id = null, int page = 0, int size = 50) =>
        new(basis, Epoch.AddYears(-2), Epoch.AddYears(3), "UTC", ScopeId: id, Page: page, PageSize: size);

    [Fact]
    public void ScopeNonZeroDetailsFilterBeforePagingWithoutChangingTotalsGapsOrExport()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(0, Reading("a", 0), Reading("idle", 0)));
        ledger.Accept(Batch(1, Reading("a", 5, 7), Reading("idle", 0)));
        ledger.Accept(Batch(61, Reading("a", 5, 7), Reading("idle", 0)));
        var all = ledger.Query(ScopeAll());
        var filtered = ledger.Query(ScopeAll(size: 1) with { NonZeroRecords = true });
        Assert.Equal(4, all.RecordCount);
        Assert.Equal(1, filtered.RecordCount);
        var row = Assert.Single(filtered.Records);
        Assert.Equal("5", row.Upload);
        Assert.Equal("7", row.Download);
        Assert.Equal(all.Upload, filtered.Upload);
        Assert.Equal(all.Download, filtered.Download);
        Assert.Equal(all.Scopes, filtered.Scopes);
        Assert.Equal(all.Series, filtered.Series);
        Assert.Equal(all.Gaps, filtered.Gaps);
        Assert.Empty(ledger.Query(ScopeAll(page: 1, size: 1) with { NonZeroRecords = true }).Records);
        Assert.Equal(4, ledger.Export(ScopeAll() with { NonZeroRecords = true }).Count);
    }

    [Fact]
    public void ScopeLedgerIsOptInAndNeverMigratesAnExistingFile()
    {
        using (var original = new Ledger(Database)) original.Accept(Sample(0, 50));
        var originalBytes = File.ReadAllBytes(Database);
        Assert.Throws<IOException>(() => ScopeLedger.CreateNew(Database));
        Assert.Throws<InvalidOperationException>(() => new ScopeLedger(Database));
        Assert.Equal(originalBytes, File.ReadAllBytes(Database));
        using (var original = new Ledger(Database)) Assert.Equal("recorded", original.Accept(Sample(1, 60)));
        var missing = Path.Combine(directory, "missing.db");
        Assert.Throws<SqliteException>(() => new ScopeLedger(missing));
        Assert.False(File.Exists(missing));
        using var scopes = ScopeLedger.CreateNew(Path.Combine(directory, "new-v2.db"));
        Assert.Empty(scopes.GetScopes("actual_member"));
        Assert.Throws<InvalidOperationException>(() => new Ledger(Path.Combine(directory, "new-v2.db")));
    }

    [Fact]
    public void ScopeBatchSeparatesDimensionsAndUsesFullCanonicalReplay()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        var business = new MeasurementScope("business", "aggregate", "business", "Synthetic business");
        var first = Batch(0, Reading("b", 700), new(business, 900, 0), Reading("a", 100));
        Assert.All(ledger.Accept(first).Scopes.Values, x => Assert.Equal("baseline", x));
        var next = Batch(1, Reading("b", 720), new(business, 930, 0), Reading("a", 110));
        ledger.Accept(next);
        Assert.Equal("duplicate", ledger.Accept(next with { Readings = next.Readings.Reverse().ToArray() }).Status);
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(1, Reading("a", 110))));
        Assert.Throws<ArgumentException>(() => ledger.Accept(next with
        {
            Readings = next.Readings.Select(x => x.Scope.IdentityValue == "a"
                ? x with { Scope = x.Scope with { Name = "Renamed" } } : x).ToArray()
        }));
        Assert.Throws<ArgumentException>(() => ledger.Accept(next with { At = Epoch.AddSeconds(0.5) }));
        var members = ledger.Query(ScopeAll());
        Assert.Equal("30", members.Upload);
        Assert.Equal(2, members.Scopes.Count);
        Assert.Equal(2, members.Scopes.Select(x => x.ScopeId).Distinct().Count());
        Assert.Equal("30", ledger.Query(ScopeAll("business")).Upload);
        Assert.Equal("0", ledger.Query(ScopeAll("routed_outbound")).Upload);
        Assert.Throws<ArgumentException>(() => ledger.Query(ScopeAll("all")));
        Assert.Throws<ArgumentException>(() => ledger.Query(ScopeAll("actual_member,business")));
    }

    [Fact]
    public void ScopeMissingKeepsIndependentCursorAndRecoversOnlyUnallocatedAcrossReopen()
    {
        long a;
        using (var ledger = ScopeLedger.CreateNew(Database))
        {
            ledger.Accept(Batch(0, Reading("a", 100), Reading("b", 200)));
            ledger.Accept(Batch(1, new ScopeReading(Member("a"), UnavailableReason: "missing_counter"), Reading("b", 210)));
            a = ledger.GetScopes("actual_member").Single(x => x.Scope.IdentityValue == "a").Id;
            Assert.Equal("10", ledger.Query(ScopeAll()).Upload);
            Assert.True(Assert.Single(ledger.Query(ScopeAll(id: a)).Gaps).IsOpen);
            Assert.Empty(ledger.Query(ScopeAll(id: ledger.GetScopes("actual_member").Single(x => x.Id != a).Id)).Gaps);
            // The missing sample still advances the instance watermark, not a fabricated cursor counter.
            Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0.5, Reading("a", 105), Reading("b", 205))));
        }
        using var reopened = new ScopeLedger(Database);
        reopened.Accept(Batch(2, Reading("a", 140), Reading("b", 220)));
        var result = reopened.Query(ScopeAll());
        Assert.Equal("60", result.Upload);
        Assert.Equal("40", result.RecoveredUpload);
        Assert.Equal("recovered-contained", Assert.Single(result.Records, x => x.ScopeId == a).Quality);
        Assert.All(result.Gaps, x => Assert.False(x.IsOpen));
    }

    [Fact]
    public void ScopeUnavailableIdentityBoundaryCannotBeUndoneByReturningToTheOldGeneration()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(0, Reading("a", 100)));
        ledger.Accept(Batch(1, new ScopeReading(Member(), UnavailableReason: "missing_counter")) with { Generation = "config-2" });
        ledger.Accept(Batch(2, Reading("a", 150)));
        Assert.Equal("0", ledger.Query(ScopeAll()).Upload);
        ledger.Accept(Batch(3, Reading("a", 157)));
        Assert.Equal("7", ledger.Query(ScopeAll()).Upload);
        Assert.Equal("0", ledger.Query(ScopeAll()).RecoveredUpload);
    }

    [Fact]
    public void ScopeRetirementRequiresTheCompleteDirectoryAndReturningScopesStartAtBaseline()
    {
        long a;
        using (var ledger = ScopeLedger.CreateNew(Database))
        {
            ledger.Accept(Batch(0, Reading("a", 100), Reading("b", 200)));
            a = ledger.GetScopes("actual_member").Single(x => x.Scope.IdentityValue == "a").Id;
            Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(1, Reading("b", 210))));
            ledger.Accept(Batch(1, Reading("b", 210)) with { RetiredScopeIds = [a] });
        }
        using var reopened = new ScopeLedger(Database);
        reopened.Accept(Batch(2, Reading("b", 220)) with { RetiredScopeIds = [a] });
        reopened.Accept(Batch(3, Reading("a", 999), Reading("b", 230)));
        Assert.Equal("30", reopened.Query(ScopeAll()).Upload);
        reopened.Accept(Batch(4, Reading("a", 1004), Reading("b", 240)));
        Assert.Equal("45", reopened.Query(ScopeAll()).Upload);
        Assert.Equal("0", reopened.Query(ScopeAll()).RecoveredUpload);
        Assert.Throws<ArgumentException>(() => reopened.Accept(Batch(5, Reading("a", 1010), Reading("b", 250))
            with { RetiredScopeIds = [a] }));
    }

    [Fact]
    public void ScopeUnavailableFirstStillRequiresRetirementAndForeignInstanceCannotRetireIt()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(0, new ScopeReading(Member(), UnavailableReason: "missing_counter")));
        var a = Assert.Single(ledger.GetScopes("actual_member")).Id;
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(1)));
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(1) with { InstanceId = "other", RetiredScopeIds = [a] }));
        ledger.Accept(Batch(1) with { RetiredScopeIds = [a] });
        ledger.Accept(Batch(2, Reading("a", 900)));
        Assert.Equal("0", ledger.Query(ScopeAll()).Upload);
        Assert.All(ledger.Query(ScopeAll()).Gaps, gap => Assert.False(gap.IsOpen));
    }

    [Fact]
    public void ScopeBatchFailureRollsBackBytesCursorGapMetadataRegistrationAndWatermark()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(0, Reading("a", 100), Reading("b", 200)));
        ledger.Accept(Batch(1, new ScopeReading(Member(), UnavailableReason: "missing_counter"), Reading("b", 210)));
        RunSql("""
            CREATE TRIGGER fail_scope_watermark BEFORE UPDATE ON scope_batches
            BEGIN SELECT RAISE(ABORT,'Synthetic batch failure'); END;
            """);
        var next = Batch(2, new ScopeReading(Member() with { Name = "Renamed synthetic member" }, 120, 0),
            Reading("b", 220), Reading("c", 300));
        Assert.Throws<SqliteException>(() => ledger.Accept(next));
        Assert.Equal(2, ledger.GetScopes("actual_member").Count);
        Assert.All(ledger.GetScopes("actual_member"), x => Assert.Equal("Synthetic member", x.Scope.Name));
        Assert.Equal("10", ledger.Query(ScopeAll()).Upload);
        Assert.True(Assert.Single(ledger.Query(ScopeAll()).Gaps).IsOpen);
        RunSql("DROP TRIGGER fail_scope_watermark;");
        ledger.Accept(next);
        Assert.Equal("40", ledger.Query(ScopeAll()).Upload);
        Assert.Equal("20", ledger.Query(ScopeAll()).RecoveredUpload);
        Assert.False(Assert.Single(ledger.Query(ScopeAll()).Gaps).IsOpen);
    }

    [Fact]
    public void ScopeMinuteRemainderConservesBytesAndBigIntegerAggregationDoesNotOverflow()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(59, Reading("a", 0), Reading("b", 0)));
        ledger.Accept(Batch(61, Reading("a", 7, 11), Reading("b", 5, 9)));
        var result = ledger.Query(ScopeAll());
        Assert.Equal("12", result.Upload);
        Assert.Equal("20", result.Download);
        Assert.Equal(4, result.RecordCount);
        Assert.All(result.Records, x => Assert.Equal("approximate", x.Quality));
        Assert.Equal(2, result.Series.Count);
        RunSql("UPDATE scope_minutes SET upload=9223372036854775807;");
        Assert.Equal((new BigInteger(long.MaxValue) * 4).ToString(CultureInfo.InvariantCulture), ledger.Query(ScopeAll()).Upload);
    }

    [Fact]
    public void ScopeMinuteUsesOnlyTheSliceForCheckedAdditionAndTrueOverflowRollsBackTheBatch()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(59, Reading("a", 0), Reading("b", 0)));
        var id = ledger.GetScopes("actual_member").Single(x => x.Scope.IdentityValue == "b").Id;
        var minute = Epoch.AddMinutes(1).UtcTicks;
        RunSql($"""
            INSERT INTO scope_minutes(start_ticks,scope_id,upload,download,approximate,synthetic)
            VALUES({minute},{id},{long.MaxValue - 5},0,0,0);
            """);
        // Second slice is 5. old+whole_delta would overflow; old+slice must not.
        ledger.Accept(Batch(61, Reading("a", 10), Reading("b", 10)));
        Assert.Equal((new BigInteger(long.MaxValue) + 15).ToString(CultureInfo.InvariantCulture), ledger.Query(ScopeAll()).Upload);
        Assert.Throws<OverflowException>(() => ledger.Accept(Batch(62, Reading("a", 20), Reading("b", 11))));
        Assert.Equal((new BigInteger(long.MaxValue) + 15).ToString(CultureInfo.InvariantCulture), ledger.Query(ScopeAll()).Upload);
        ledger.Accept(Batch(62, Reading("a", 20), Reading("b", 10)));
        Assert.Equal((new BigInteger(long.MaxValue) + 25).ToString(CultureInfo.InvariantCulture), ledger.Query(ScopeAll()).Upload);
    }

    [Fact]
    public void ScopeRecoveryQueryDisclosesPartialAndCrossMidnightAmountsWithoutApportionment()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(59, Reading("a", 100)));
        ledger.Accept(Batch(72, Reading("a", 230)));
        var all = ledger.Query(ScopeAll());
        Assert.Equal("130", all.Upload);
        Assert.Equal("130", all.RecoveredUpload);
        Assert.Equal("130", all.UnbucketedUpload);
        Assert.Empty(all.Series);
        var partial = ledger.Query(ScopeAll() with { Start = Epoch.AddMinutes(1), End = Epoch.AddMinutes(2) });
        Assert.Equal("0", partial.Upload);
        Assert.Equal("130", partial.PartialUpload);
        Assert.Equal("recovered-partial", Assert.Single(partial.Records).Quality);
        Assert.Single(partial.Gaps);
    }

    [Fact]
    public void ScopeQueryPagesNeverTrimTotalsOrCoverageGapsAndKeepsBasisInEachRow()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(0, Reading("a", 0) with { PartialCoverage = true }, Reading("b", 0)));
        ledger.Accept(Batch(1, Reading("a", 10) with { PartialCoverage = true }, Reading("b", 20)));
        ledger.Accept(Batch(61, Reading("a", 70) with { PartialCoverage = true }, Reading("b", 80)));
        var first = ledger.Query(ScopeAll(size: 1));
        var second = ledger.Query(ScopeAll(page: 1, size: 1));
        Assert.Equal("150", first.Upload);
        Assert.Equal(first.Upload, second.Upload);
        Assert.Equal(first.Gaps, second.Gaps);
        Assert.Equal(first.RecordCount, second.RecordCount);
        Assert.Single(first.Records);
        Assert.Single(second.Records);
        Assert.NotEqual(first.Records[0].ScopeId, second.Records[0].ScopeId);
        var a = ledger.GetScopes("actual_member").Single(x => x.Scope.IdentityValue == "a").Id;
        Assert.Equal("70", ledger.Query(ScopeAll(id: a)).Upload);
        Assert.All(ledger.Query(ScopeAll(id: a)).Records, x =>
        {
            Assert.Equal("actual_member", x.Basis); Assert.True(x.PartialCoverage);
        });
        Assert.Empty(ledger.Query(ScopeAll("business", a)).Records);
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Query(ScopeAll(page: -1)));
    }

    [Fact]
    public void ScopeBoundaryResetAndExplicitInvalidationNeverRecoverUncertainCumulativeBytes()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(0, Reading("a", 100)));
        ledger.Accept(Batch(1, Reading("a", 1000)) with { SessionId = "process-2" });
        ledger.Accept(Batch(2, Reading("a", 1007)) with { SessionId = "process-2" });
        ledger.Accept(Batch(3, Reading("a", 0)) with { SessionId = "process-2" });
        ledger.Accept(Batch(4, Reading("a", 900) with { InvalidateContinuity = true }) with { SessionId = "process-2" });
        Assert.Equal("7", ledger.Query(ScopeAll()).Upload);
        Assert.Contains(ledger.Query(ScopeAll()).Gaps, x => x.Reason == "counter_reset");
        Assert.Equal("0", ledger.Query(ScopeAll()).RecoveredUpload);
    }

    [Fact]
    public void ScopeIdentityValidationRejectsGuessedAssociationsAndMalformedPairs()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0, new ScopeReading(Member(), 5, null))));
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0, new ScopeReading(Member(), -1, 0))));
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0, new ScopeReading(Member(), 5, 0, "missing_counter"))));
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0, new ScopeReading(Member(), UnavailableReason: "https://secret.invalid"))));
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0, new ScopeReading(Member() with { NodeId = "other" }, 0, 0))));
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0, Reading("a", 0), Reading("a", 1))));
        var digest = XrayParser.Hash("synthetic-configuration");
        var independent = new MeasurementScope("actual_member", "outbound",
            System.Text.Json.JsonSerializer.Serialize(new[] { digest, "fixture-independent" }),
            "Synthetic member", OutboundTag: "fixture-independent");
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0, new ScopeReading(independent with { NodeId = "guessed" }, 0, 0))));
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0, new ScopeReading(independent with { OutboundTag = "wrong" }, 0, 0))));
        ledger.Accept(Batch(0, new ScopeReading(independent, 5, 0)));
        ledger.Accept(Batch(1, new ScopeReading(independent, 9, 0)));
        Assert.Null(Assert.Single(ledger.GetScopes("actual_member")).Scope.NodeId);
        Assert.Equal("4", ledger.Query(ScopeAll()).Upload);
    }

    [Fact]
    public void ScopeSchemaVersionAndIntegerGuardsAreEnforcedOnReopenAndWrite()
    {
        using (var ledger = ScopeLedger.CreateNew(Database)) ledger.Accept(Batch(0, Reading("a", 0)));
        Assert.Throws<SqliteException>(() => RunSql("UPDATE scope_cursors SET upload=1.5;"));
        Assert.Throws<SqliteException>(() => RunSql("UPDATE scope_cursors SET continuity_valid=2;"));
        Assert.Throws<SqliteException>(() => RunSql("UPDATE measurement_scopes SET basis='business';"));
        Assert.Throws<SqliteException>(() => RunSql("""
            INSERT INTO scope_minutes(start_ticks,scope_id,upload,download,approximate,synthetic)
            VALUES(0,1,9223372036854775808,0,0,0);
            """));
        RunSql("DROP INDEX scope_gap_open;");
        Assert.Throws<InvalidOperationException>(() => new ScopeLedger(Database));
    }

    [Fact]
    public void ScopeInstancesAndConcurrentReplayRemainIndependent()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(0, Reading("a", 0)));
        ledger.Accept(Batch(0, Reading("a", 100)) with { InstanceId = "other" });
        var next = Batch(1, Reading("a", 10));
        Parallel.For(0, 8, _ => ledger.Accept(next));
        ledger.Accept(Batch(1, Reading("a", 120)) with { InstanceId = "other" });
        Assert.Equal("30", ledger.Query(ScopeAll()).Upload);
        Assert.Equal(2, ledger.GetScopes("actual_member").Count);
        Assert.Equal(2, ledger.Query(ScopeAll()).RecordCount);
    }

    [Fact]
    public void ScopeRenameKeepsArchivedNodeMetadataAndNeverClosesLegacyGaps()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        ledger.Accept(Batch(0, Reading("a", 0), Reading("b", 0)));
        var a = ledger.GetScopes("actual_member").Single(x => x.Scope.IdentityValue == "a").Id;
        RunSql($"""
            INSERT INTO scope_gaps(start_ticks,end_ticks,scope_id,reason,is_open,origin)
            VALUES({Epoch.UtcTicks},{Epoch.UtcTicks},NULL,'legacy_unknown',1,'legacy'),
                  ({Epoch.UtcTicks},{Epoch.UtcTicks},{a},'legacy_scoped',1,'legacy');
            """);
        ledger.Accept(Batch(1, new ScopeReading(Member() with { Name = "Renamed synthetic" }, 10, 0), Reading("b", 20)));
        var result = ledger.Query(ScopeAll(id: a));
        Assert.Equal("Renamed synthetic", Assert.Single(result.Scopes).Name);
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, x => Assert.True(x.IsOpen));
        // Even a page after the last record must disclose an old open, unassigned gap.
        Assert.Single(ledger.Query(ScopeAll("business", page: 10)).Gaps);
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM nodes WHERE node_key='instance/a';";
        Assert.Equal("Synthetic member", command.ExecuteScalar());
    }

    [Fact]
    public void ScopeIndependentConfigurationChangeRequiresRetirementAndDoesNotMergeSameNames()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        MeasurementScope Outbound(string config) => new("actual_member", "outbound",
            System.Text.Json.JsonSerializer.Serialize(new[] { XrayParser.Hash(config), "fixture" }),
            "Same synthetic name", OutboundTag: "fixture");
        var first = Outbound("synthetic-config-a");
        var second = Outbound("synthetic-config-b");
        ledger.Accept(Batch(0, new ScopeReading(first, 100, 0)));
        var id = Assert.Single(ledger.GetScopes("actual_member")).Id;
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(1, new ScopeReading(second, 500, 0))
            with { Generation = "config-2" }));
        Assert.Single(ledger.GetScopes("actual_member"));
        ledger.Accept(Batch(1, new ScopeReading(second, 500, 0)) with { Generation = "config-2", RetiredScopeIds = [id] });
        var secondId = ledger.GetScopes("actual_member").Single(x => x.Id != id).Id;
        ledger.Accept(Batch(2, new ScopeReading(second, 507, 0)) with { Generation = "config-2", RetiredScopeIds = [id] });
        ledger.Accept(Batch(3, new ScopeReading(first, 800, 0)) with { RetiredScopeIds = [secondId] });
        Assert.Equal("7", ledger.Query(ScopeAll()).Upload);
        Assert.Equal(2, ledger.GetScopes("actual_member").Count);
        Assert.Equal("0", ledger.Query(ScopeAll()).RecoveredUpload);
    }

    [Fact]
    public void ScopeBatchRejectsTwoIdentitiesForTheSameMemberCounterTag()
    {
        using var ledger = ScopeLedger.CreateNew(Database);
        var duplicateTag = new ScopeReading(Member("b") with { OutboundTag = "fixture-a" }, 100, 0);
        Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0, Reading("a", 100), duplicateTag)));
        Assert.Empty(ledger.GetScopes("actual_member"));
        var route = new MeasurementScope("routed_outbound", "outbound",
            System.Text.Json.JsonSerializer.Serialize(new[] { XrayParser.Hash("synthetic-config"), "fixture-a" }),
            "Synthetic route", OutboundTag: "fixture-a");
        ledger.Accept(Batch(0, Reading("a", 100), new(route, 200, 0)));
        ledger.Accept(Batch(1, Reading("a", 107), new(route, 220, 0)));
        Assert.Equal("7", ledger.Query(ScopeAll()).Upload);
        Assert.Equal("20", ledger.Query(ScopeAll("routed_outbound")).Upload);
    }

    [Fact]
    public void ScopeMigrationBacksUpAndPreservesHistoryWhileInvalidatingLegacyContinuity()
    {
        using (var old = new Ledger(Database))
        {
            old.Accept(Sample(0, 100, 200));
            old.Accept(Sample(1, 110, 220));
            old.Accept(Sample(61, 160, 280));
            old.RecordGap(Epoch.AddSeconds(62), "interface_unavailable");
        }
        RunSql("UPDATE minutes SET upload=9007199254740993,approximate=1,synthetic=1;");
        var backup = Path.Combine(directory, "before-v2.db");
        var provider = Path.Combine(directory, "provider.db");
        using (var imports = new ProviderImports(provider))
            imports.Commit(new("Synthetic provider", "UTC",
                "node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\nSynthetic node,0.125,9007199254740993,1125899906842624.125,synthetic,2024-02-28 23:59:00\n"));
        var providerBytes = File.ReadAllBytes(provider);
        var migration = ScopeLedgerMigration.Migrate(Database, backup);
        Assert.Equal(1, migration.LegacyScopes);
        Assert.Equal(providerBytes, File.ReadAllBytes(provider));
        Assert.Throws<InvalidOperationException>(() => new Ledger(Database));
        using var original = new Ledger(backup);
        using var migrated = new ScopeLedger(Database);
        var result = migrated.Query(ScopeAll("legacy_proxy"));
        Assert.Equal(original.Query(All()).Upload, result.Upload);
        Assert.Equal(original.Query(All()).RecoveredUpload, result.RecoveredUpload);
        Assert.Equal(original.Query(All()).RecordCount, result.RecordCount);
        Assert.Contains(result.Gaps, x => x.ScopeId is null && x.Origin == "legacy" && x.IsOpen);
        var legacy = new MeasurementScope("legacy_proxy", "legacy_node", "instance/node", "Synthetic node", "node");
        migrated.Accept(Batch(63, new ScopeReading(legacy, 900, 1200)));
        Assert.Equal(result.Upload, migrated.Query(ScopeAll("legacy_proxy")).Upload);
        Assert.Contains(migrated.Query(ScopeAll("legacy_proxy")).Gaps, x => x.Origin == "legacy" && x.IsOpen);
    }

    [Fact]
    public void ScopeMigrationSupportsEmptyV1AndDoesNotReplaceExistingBackupOrAcceptV2()
    {
        using (new Ledger(Database)) { }
        var backup = Path.Combine(directory, "before.db");
        File.WriteAllText(backup, "Synthetic backup already exists");
        Assert.Throws<IOException>(() => ScopeLedgerMigration.Migrate(Database, backup));
        Assert.Equal("Synthetic backup already exists", File.ReadAllText(backup));
        using (var old = new Ledger(Database)) Assert.Empty(old.GetNodes());
        var freshBackup = Path.Combine(directory, "new-backup-directory", "fresh-backup.db");
        Assert.Equal(0, ScopeLedgerMigration.Migrate(Database, freshBackup).LegacyScopes);
        Assert.Throws<InvalidOperationException>(() => ScopeLedgerMigration.Migrate(Database, Path.Combine(directory, "second.db")));
        Assert.False(File.Exists(Path.Combine(directory, "second.db")));
    }

    [Theory]
    [InlineData("UPDATE cursors SET upload=1.5;")]
    [InlineData("UPDATE cursors SET interrupted=2;")]
    [InlineData("UPDATE cursors SET instance_id='wrong-instance';")]
    [InlineData("INSERT INTO gaps VALUES(999,0,1,'orphan','synthetic',0);")]
    [InlineData("ALTER TABLE nodes ADD COLUMN unexpected TEXT;")]
    [InlineData("CREATE TABLE import_batches(synthetic TEXT);")]
    public void ScopeMigrationPreflightRejectsUnsupportedHistoryBeforeCreatingBackup(string mutation)
    {
        using (var old = new Ledger(Database)) old.Accept(Sample(0, 10));
        RunSql(mutation);
        var before = File.ReadAllBytes(Database);
        var backup = Path.Combine(directory, "rejected-backup.db");
        Assert.Throws<InvalidOperationException>(() => ScopeLedgerMigration.Migrate(Database, backup));
        Assert.False(File.Exists(backup));
        Assert.Equal(before, File.ReadAllBytes(Database));
    }

    [Theory]
    [InlineData("INSERT INTO scope_minutes VALUES(0,999,0,0,0,0);")]
    [InlineData("UPDATE scope_minutes SET upload=upload+1;")]
    public void ScopeMigrationRollsBackDdlAndVersionAfterSqlOrVerificationFailure(string injectedSql)
    {
        using (var old = new Ledger(Database))
        {
            old.Accept(Sample(0, 100));
            old.Accept(Sample(1, 110));
        }
        var backupPath = Path.Combine(directory, "synthetic-backup.db");
        using var source = new SqliteConnection($"Data Source={Database};Pooling=False");
        source.Open();
        using var backup = new SqliteConnection($"Data Source={backupPath};Pooling=False");
        backup.Open();
        source.BackupDatabase(backup);
        using (var command = source.CreateCommand()) { command.CommandText = "PRAGMA foreign_keys=ON;"; command.ExecuteNonQuery(); }
        using var stream = typeof(ScopeLedger).Assembly.GetManifestResourceStream("V2rayNMonitor.Core.ScopeLedgerV2.sql")!;
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd() + "\n" + injectedSql;
        var apply = typeof(ScopeLedgerMigration).GetMethod("Apply",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Throws<System.Reflection.TargetInvocationException>(() => apply.Invoke(null, [source, backup, sql]));
        using var check = source.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'scope_%' OR name='measurement_scopes';";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = "SELECT upload FROM minutes;";
        Assert.Equal(10L, check.ExecuteScalar());
    }

    [Fact]
    public void ScopeMigrationRejectsAChangedSnapshotRatherThanMigratingPastTheBackup()
    {
        using (var old = new Ledger(Database)) old.Accept(Sample(0, 100));
        using var source = new SqliteConnection($"Data Source={Database};Pooling=False");
        source.Open();
        using var backup = new SqliteConnection($"Data Source={Path.Combine(directory, "synthetic-backup.db")};Pooling=False");
        backup.Open();
        source.BackupDatabase(backup);
        using (var update = source.CreateCommand()) { update.CommandText = "UPDATE cursors SET upload=150;"; update.ExecuteNonQuery(); }
        using var stream = typeof(ScopeLedger).Assembly.GetManifestResourceStream("V2rayNMonitor.Core.ScopeLedgerV2.sql")!;
        using var reader = new StreamReader(stream);
        var apply = typeof(ScopeLedgerMigration).GetMethod("Apply",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Throws<System.Reflection.TargetInvocationException>(() => apply.Invoke(null, [source, backup, reader.ReadToEnd()]));
        using var check = source.CreateCommand(); check.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, check.ExecuteScalar());
    }

    private static MeasurementScope ProbeMember(string id = "a") =>
        Member(id) with { Basis = "probe_urltest_member" };
    private static MeasurementScope ProbeTotal(string total = "business") =>
        new("probe_urltest_" + total, "aggregate", "probe_urltest_" + total, "Synthetic URLTest " + total);

    [Fact]
    public void ProbeLedgerRequiresExplicitV3AndNeverUpgradesV2OnOpen()
    {
        using (var ledger = ScopeLedger.CreateNew(Database))
        {
            Assert.False(ledger.SupportsProbes);
            Assert.Throws<ArgumentException>(() => ledger.Accept(Batch(0, new ScopeReading(ProbeMember(), 50, 0))));
            Assert.Throws<ArgumentException>(() => ledger.Query(ScopeAll("probe_urltest_member")));
        }
        var before = HistorySnapshot(Database);
        using (var reopened = new ScopeLedger(Database)) Assert.False(reopened.SupportsProbes);
        Assert.Equal(before, HistorySnapshot(Database));
        Assert.Throws<IOException>(() => ScopeLedger.CreateNew(Database, includeProbes: true));
        var path = Path.Combine(directory, "explicit-v3.db");
        using (var probes = ScopeLedger.CreateNew(path, includeProbes: true))
        {
            Assert.True(probes.SupportsProbes);
            Assert.Equal(10, probes.Bases.Count);
            Assert.Empty(probes.GetScopes("probe_urltest_member"));
        }
        using (var reopened = new ScopeLedger(path)) Assert.True(reopened.SupportsProbes);
        RunSql("PRAGMA user_version=3;");
        Assert.Throws<InvalidOperationException>(() => new ScopeLedger(Database));
    }

    [Fact]
    public void ProbeLedgerSeparatesBusinessMembersTotalsAndExactLargeIntegers()
    {
        using var ledger = ScopeLedger.CreateNew(Database, includeProbes: true);
        const long initial = 9007199254740993;
        ScopeBatch Complete(int second, long business, long probe) => Batch(second,
            Reading("a", business), new(ProbeMember(), probe, 0), new(ProbeTotal(), probe, 0),
            new(ProbeTotal("excluded"), 0, 0), new(ProbeTotal("unknown"), 0, 0));
        Assert.All(ledger.Accept(Complete(0, 100, initial)).Scopes.Values, x => Assert.Equal("baseline", x));
        var next = Complete(1, 130, initial + 7);
        ledger.Accept(next);
        Assert.Equal("duplicate", ledger.Accept(next).Status);
        Assert.Equal("30", ledger.Query(ScopeAll()).Upload);
        Assert.Equal("7", ledger.Query(ScopeAll("probe_urltest_member")).Upload);
        Assert.Equal("7", ledger.Query(ScopeAll("probe_urltest_business")).Upload);
        Assert.Equal("0", ledger.Query(ScopeAll("probe_urltest_excluded")).Upload);
        Assert.Equal("0", ledger.Query(ScopeAll("probe_urltest_unknown")).Upload);
        Assert.All(ledger.Export(ScopeAll("probe_urltest_member")), row => Assert.Equal("probe_urltest_member", row.Basis));
        Assert.Throws<ArgumentException>(() => ledger.Query(ScopeAll("actual_member,probe_urltest_member")));
        Assert.Throws<ArgumentException>(() => ledger.Accept(Complete(2, 140, initial + 10) with
        {
            Readings = [Reading("a", 140), new(ProbeMember() with { NodeId = "guessed" }, initial + 10, 0)]
        }));
        Assert.Throws<SqliteException>(() => RunSql("UPDATE scope_cursors SET upload=1.5 WHERE scope_id=2;"));
    }

    [Fact]
    public void ProbeLedgerMissingRecoveryAndGenerationChangesRemainSeparateAcrossReopen()
    {
        using (var ledger = ScopeLedger.CreateNew(Database, includeProbes: true))
        {
            ledger.Accept(Batch(0, Reading("a", 100), new(ProbeMember(), 500, 0)));
            ledger.Accept(Batch(1, Reading("a", 110), new(ProbeMember(), UnavailableReason: "missing_counter")));
            Assert.True(Assert.Single(ledger.Query(ScopeAll("probe_urltest_member")).Gaps).IsOpen);
            Assert.Empty(ledger.Query(ScopeAll()).Gaps);
        }
        using var reopened = new ScopeLedger(Database);
        reopened.Accept(Batch(2, Reading("a", 120), new(ProbeMember(), 550, 0)));
        var recovered = reopened.Query(ScopeAll("probe_urltest_member"));
        Assert.Equal("50", recovered.Upload);
        Assert.Equal("50", recovered.RecoveredUpload);
        Assert.All(recovered.Gaps, x => Assert.False(x.IsOpen));
        Assert.Equal("20", reopened.Query(ScopeAll()).Upload);
        reopened.Accept(Batch(3, Reading("a", 130), new(ProbeMember(), UnavailableReason: "missing_counter"))
            with { Generation = "config-2" });
        reopened.Accept(Batch(4, Reading("a", 140), new(ProbeMember(), 900, 0)));
        Assert.Equal("50", reopened.Query(ScopeAll("probe_urltest_member")).Upload);
        reopened.Accept(Batch(5, Reading("a", 150), new(ProbeMember(), 905, 0)));
        Assert.Equal("55", reopened.Query(ScopeAll("probe_urltest_member")).Upload);
    }

    [Fact]
    public void ProbeLedgerOverflowRollsBackBusinessNamesScopesAndCursorsTogether()
    {
        using var ledger = ScopeLedger.CreateNew(Database, includeProbes: true);
        ledger.Accept(Batch(0, Reading("a", 0), new(ProbeMember(), 0, 0)));
        ledger.Accept(Batch(1, Reading("a", 10), new(ProbeMember(), 5, 0)));
        RunSql("UPDATE scope_minutes SET upload=9223372036854775807 WHERE scope_id IN " +
            "(SELECT scope_id FROM measurement_scopes WHERE basis='probe_urltest_member');");
        var before = HistorySnapshot(Database);
        Assert.Throws<OverflowException>(() => ledger.Accept(Batch(2,
            new(Member() with { Name = "Not committed" }, 20, 0), Reading("new", 100),
            new(ProbeMember(), 6, 0))));
        Assert.Equal(before, HistorySnapshot(Database));
        Assert.Equal("10", ledger.Query(ScopeAll()).Upload);
        Assert.Equal("Synthetic member", Assert.Single(ledger.GetScopes("actual_member")).Scope.Name);
    }

    [Fact]
    public void ProbeLedgerCommitRetiresMissingProbeScopesAndReturningMembersStartAtBaseline()
    {
        using var ledger = ScopeLedger.CreateNew(Database, includeProbes: true);
        ScopeCollector.Commit(ledger, Batch(0, Reading("a", 0), new(ProbeMember(), 100, 0)));
        ScopeCollector.Commit(ledger, Batch(1, Reading("a", 10), new(ProbeMember(), 110, 0)));
        ScopeCollector.Commit(ledger, Batch(2, Reading("a", 20)));
        Assert.True(Assert.Single(ledger.Query(ScopeAll("probe_urltest_member")).Gaps).IsOpen);
        ScopeCollector.Commit(ledger, Batch(3, Reading("a", 30), new(ProbeMember(), 900, 0)));
        ScopeCollector.Commit(ledger, Batch(4, Reading("a", 40), new(ProbeMember(), 905, 0)));
        Assert.Equal("15", ledger.Query(ScopeAll("probe_urltest_member")).Upload);
        Assert.Equal("40", ledger.Query(ScopeAll()).Upload);
    }

    [Fact]
    public void ProbeMigrationPreservesEveryOldRowAndBusinessContinuityWithoutBackfillingProbes()
    {
        using (var old = new Ledger(Database))
        {
            old.Accept(Sample(0, 100));
            old.Accept(Sample(1, 110));
            old.RecordGap(Epoch.AddSeconds(2), "legacy_gap");
        }
        ScopeLedgerMigration.Migrate(Database, Path.Combine(directory, "before-v2.db"));
        using (var ledger = new ScopeLedger(Database))
        {
            ScopeCollector.Commit(ledger, Batch(3, Reading("a", 100)));
            ScopeCollector.Commit(ledger, Batch(4, Reading("a", 120)));
            ScopeCollector.Commit(ledger, Batch(65, Reading("a", 150)));
            ScopeCollector.Commit(ledger, Batch(66, Reading("a", 151),
                new(ProbeTotal() with { Basis = "unknown", IdentityValue = "unknown" }, UnavailableReason: "missing_counter")));
        }
        RunSql("UPDATE scope_minutes SET upload=9007199254740993,approximate=1,synthetic=1;");
        var providerPath = Path.Combine(directory, "provider.db");
        using (var imports = new ProviderImports(providerPath))
            imports.Commit(new("Synthetic provider", "UTC",
                "node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\nSynthetic node,0.125,9007199254740993,1125899906842624.125,synthetic,2024-02-28 23:59:00\n"));
        var providerBefore = File.ReadAllBytes(providerPath);
        var oldRows = HistorySnapshot(Database, includeSchema: false);
        var oldSchema = HistorySnapshot(Database);
        var backup = Path.Combine(directory, "v2-backup", "before-probes.db");
        var migration = ScopeLedgerMigration.MigrateProbes(Database, backup);
        Assert.Equal(3, migration.PreservedScopes);
        Assert.Equal(oldSchema, HistorySnapshot(backup));
        Assert.Equal(oldRows, HistorySnapshot(Database, includeSchema: false));
        Assert.Equal(providerBefore, File.ReadAllBytes(providerPath));
        using var upgraded = new ScopeLedger(Database);
        Assert.True(upgraded.SupportsProbes);
        var oldBusiness = upgraded.Query(ScopeAll()).Upload;
        var unknown = new MeasurementScope("unknown", "aggregate", "unknown", "Synthetic URLTest business");
        ScopeCollector.Commit(upgraded, Batch(67, Reading("a", 161),
            new(unknown, UnavailableReason: "missing_counter"), new(ProbeMember(), 800, 0)));
        Assert.Equal((BigInteger.Parse(oldBusiness, CultureInfo.InvariantCulture) + 10).ToString(CultureInfo.InvariantCulture),
            upgraded.Query(ScopeAll()).Upload);
        Assert.Equal("0", upgraded.Query(ScopeAll("probe_urltest_member")).Upload);
        ScopeCollector.Commit(upgraded, Batch(68, Reading("a", 166),
            new(unknown, UnavailableReason: "missing_counter"), new(ProbeMember(), 807, 0)));
        Assert.Equal("7", upgraded.Query(ScopeAll("probe_urltest_member")).Upload);
        Assert.Contains(upgraded.Query(ScopeAll("legacy_proxy")).Gaps, x => x.Origin == "legacy" && x.IsOpen);
    }

    [Fact]
    public void ProbeMigrationSupportsEmptyV2AndRefusesExistingBackupV1ProviderOrV3()
    {
        using (ScopeLedger.CreateNew(Database)) { }
        var backup = Path.Combine(directory, "before-probes.db");
        File.WriteAllText(backup, "Synthetic existing backup");
        Assert.Throws<IOException>(() => ScopeLedgerMigration.MigrateProbes(Database, backup));
        Assert.Equal("Synthetic existing backup", File.ReadAllText(backup));
        Assert.Equal(0, ScopeLedgerMigration.MigrateProbes(Database, Path.Combine(directory, "fresh.db")).PreservedScopes);
        var v1 = Path.Combine(directory, "v1.db");
        var provider = Path.Combine(directory, "provider.db");
        using (new Ledger(v1)) { }
        using (new ProviderImports(provider)) { }
        foreach (var path in new[] { Database, v1, provider })
        {
            var before = File.ReadAllBytes(path);
            var rejected = path + ".rejected-backup";
            Assert.Throws<InvalidOperationException>(() => ScopeLedgerMigration.MigrateProbes(path, rejected));
            Assert.False(File.Exists(rejected));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    [Theory]
    [InlineData("ALTER TABLE nodes ADD COLUMN unexpected TEXT;")]
    [InlineData("CREATE TABLE import_batches(synthetic TEXT);")]
    [InlineData("DROP INDEX scope_gap_open;")]
    public void ProbeMigrationRejectsUnsupportedV2BeforeCreatingBackup(string mutation)
    {
        using (ScopeLedger.CreateNew(Database)) { }
        RunSql(mutation);
        var before = File.ReadAllBytes(Database);
        var backup = Path.Combine(directory, "rejected.db");
        Assert.Throws<InvalidOperationException>(() => ScopeLedgerMigration.MigrateProbes(Database, backup));
        Assert.False(File.Exists(backup));
        Assert.Equal(before, File.ReadAllBytes(Database));
    }

    [Theory]
    [InlineData("SELECT * FROM synthetic_missing_table;")]
    [InlineData("UPDATE scope_minutes SET upload=upload+1;")]
    [InlineData("INSERT INTO scope_minutes VALUES(0,999,0,0,0,0);")]
    public void ProbeMigrationRollsBackRebuiltDirectoryAndVersionAfterSqlDataOrForeignKeyFailure(string injectedSql)
    {
        using (var ledger = ScopeLedger.CreateNew(Database))
        {
            ledger.Accept(Batch(0, Reading("a", 0)));
            ledger.Accept(Batch(1, Reading("a", 10)));
        }
        var before = HistorySnapshot(Database);
        ApplyProbeUpgradeForTest(injectedSql);
        Assert.Equal(before, HistorySnapshot(Database));
        using var reopened = new ScopeLedger(Database);
        Assert.False(reopened.SupportsProbes);
    }

    [Fact]
    public void ProbeMigrationRejectsChangesAfterBackupWithoutDiscardingNewerData()
    {
        using (var ledger = ScopeLedger.CreateNew(Database)) ledger.Accept(Batch(0, Reading("a", 100)));
        ApplyProbeUpgradeForTest("", changeAfterBackup: true);
        using var check = new SqliteConnection($"Data Source={Database};Pooling=False");
        check.Open();
        using var command = check.CreateCommand();
        command.CommandText = "SELECT upload FROM scope_cursors;";
        Assert.Equal(150L, command.ExecuteScalar());
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    private void ApplyProbeUpgradeForTest(string injectedSql, bool changeAfterBackup = false)
    {
        using var source = new SqliteConnection($"Data Source={Database};Pooling=False");
        source.Open();
        using var snapshot = new SqliteConnection($"Data Source={Path.Combine(directory, "synthetic-v2-backup.db")};Pooling=False");
        snapshot.Open();
        source.BackupDatabase(snapshot);
        using (var command = source.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys=OFF;" +
                (changeAfterBackup ? "UPDATE scope_cursors SET upload=150;" : "");
            command.ExecuteNonQuery();
        }
        using var stream = typeof(ScopeLedger).Assembly.GetManifestResourceStream("V2rayNMonitor.Core.ScopeLedgerV3.sql")!;
        using var reader = new StreamReader(stream);
        var apply = typeof(ScopeLedgerMigration).GetMethod("ApplyProbes",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            apply.Invoke(null, [source, snapshot, reader.ReadToEnd() + "\n" + injectedSql]));
    }

    // Logical values retain SQLite types and ordering; only the directory DDL/version should change on upgrade.
    private static string HistorySnapshot(string path, bool includeSchema = true)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        var queries = new List<string>();
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) queries.Add($"SELECT * FROM \"{reader.GetString(0)}\" ORDER BY rowid;");
        }
        if (includeSchema)
        {
            queries.Add("SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name;");
            queries.Add("PRAGMA user_version;");
        }
        var result = new List<object?[]>();
        foreach (var query in queries)
        {
            using var command = db.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add([query, .. Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i))]);
        }
        return System.Text.Json.JsonSerializer.Serialize(result);
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
