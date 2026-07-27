using System.Diagnostics.Metrics;
using FluentAssertions;
using MistChess.Api.Application;

namespace MistChess.Api.Tests;

public sealed class Phase2PolicyTests
{
    [Fact]
    public void Elo_uses_provisional_and_established_k_factors()
    {
        RatingService.Calculate(1500, 1500, 0, 1).After.Should().Be(1520);
        RatingService.Calculate(1500, 1500, 0, 0).After.Should().Be(1480);
        RatingService.Calculate(1500, 1500, 0, 0.5).After.Should().Be(1500);
        RatingService.Calculate(1500, 1500, 20, 1).After.Should().Be(1510);
        RatingService.Calculate(1500, 1500, 20, 0).After.Should().Be(1490);
    }

    [Fact]
    public void Elo_handles_upsets_unequal_ratings_and_mixed_k_factors()
    {
        RatingService.Calculate(1800, 1200, 0, 1).After.Should().Be(1801);
        RatingService.Calculate(1200, 1800, 0, 1).After.Should().Be(1239);
        RatingService.Calculate(1800, 1200, 20, 0).After.Should().Be(1781);
        RatingService.Calculate(1200, 1800, 20, 1).After.Should().Be(1219);
        RatingService.Calculate(1800, 1200, 20, 0.5).After.Should().Be(1791);
        RatingService.Calculate(1200, 1800, 20, 0.5).After.Should().Be(1209);
    }

    [Fact]
    public void Elo_never_falls_below_the_rating_floor()
    {
        RatingService.Calculate(100, 3000, 100, 0).After.Should().Be(100);
    }

    [Theory]
    [InlineData(2, 0, null, "2-4", true)]
    [InlineData(4, 0, null, "2-4", true)]
    [InlineData(5, 0, 400, "5-9", false)]
    [InlineData(9, 0, 400, "5-9", false)]
    [InlineData(5, 15, 500, "5-9", false)]
    [InlineData(5, 30, 600, "5-9", false)]
    [InlineData(5, 45, 800, "5-9", false)]
    [InlineData(10, 0, 250, "10-19", false)]
    [InlineData(19, 0, 250, "10-19", false)]
    [InlineData(10, 15, 350, "10-19", false)]
    [InlineData(10, 30, 450, "10-19", false)]
    [InlineData(10, 45, 650, "10-19", false)]
    [InlineData(20, 0, 150, "20-49", false)]
    [InlineData(49, 0, 150, "20-49", false)]
    [InlineData(20, 15, 250, "20-49", false)]
    [InlineData(20, 30, 350, "20-49", false)]
    [InlineData(20, 45, 550, "20-49", false)]
    [InlineData(50, 0, 100, "50+", false)]
    [InlineData(50, 15, 200, "50+", false)]
    [InlineData(50, 30, 300, "50+", false)]
    [InlineData(50, 45, 500, "50+", false)]
    [InlineData(50, 60, null, "50+", true)]
    public void Matchmaking_range_follows_population_and_waiting_bands(
        int population,
        int waitingSeconds,
        int? expectedRadius,
        string expectedBand,
        bool unrestricted)
    {
        var range = MatchmakingPolicy.Calculate(population, TimeSpan.FromSeconds(waitingSeconds));

        range.EffectiveRadius.Should().Be(expectedRadius);
        range.PopulationBand.Should().Be(expectedBand);
        range.IsUnrestricted.Should().Be(unrestricted);
    }

    [Fact]
    public void Production_meter_emits_release_monitoring_measurements()
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == MistChessMetrics.MeterName)
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, _, _, _) => observed.Add(instrument.Name));
        listener.SetMeasurementEventCallback<int>(
            (instrument, _, _, _) => observed.Add(instrument.Name));
        listener.SetMeasurementEventCallback<double>(
            (instrument, _, _, _) => observed.Add(instrument.Name));
        listener.Start();

        using var metrics = new MistChessMetrics();
        var searchRange = MatchmakingPolicy.Calculate(50, TimeSpan.FromSeconds(60));
        metrics.RecordMatchmakingScan(50, searchRange, 60_000, foundCandidate: true);
        metrics.RecordMatchmakingTicket("created", waitingMilliseconds: null);
        metrics.RecordMatchmakingTicket("matched", waitingMilliseconds: 60_000);
        metrics.RecordMatch(searchRange.PopulationBand, searchRange.IsUnrestricted, 320, 60_000);
        metrics.RecordClockTimeout("600+5", 125, duplicate: false);
        metrics.RecordRatingSettlement(reused: false, redDelta: 20, blackDelta: -20);
        metrics.RecordHistoryList(12);
        metrics.RecordReplayBuild(shared: true, frames: 42, elapsedMilliseconds: 18);
        metrics.RecordReplayResponseSize(compressed: true, bytes: 4096, statusCode: 200);
        metrics.RecordReplayCacheValidation(hit: true);
        metrics.RecordShareOperation("read_valid");

        observed.Should().Contain(
        [
            "mistchess.matchmaking.scans",
            "mistchess.matchmaking.eligible_population",
            "mistchess.matchmaking.waiting.duration",
            "mistchess.matchmaking.tickets",
            "mistchess.matchmaking.ticket.duration",
            "mistchess.matchmaking.matches",
            "mistchess.matchmaking.rating.difference",
            "mistchess.matchmaking.match.duration",
            "mistchess.clock.timeouts",
            "mistchess.clock.scan.delay",
            "mistchess.rating.settlements",
            "mistchess.rating.change",
            "mistchess.history.list.duration",
            "mistchess.replay.build.duration",
            "mistchess.replay.frames",
            "mistchess.replay.response.size",
            "mistchess.replay.cache.validations",
            "mistchess.share.operations",
        ]);
    }
}
