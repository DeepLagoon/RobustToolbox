using Moq;
using NUnit.Framework;
using Robust.Server;
using Robust.Shared.Log;
using Robust.Shared.Timing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Robust.UnitTesting.Server.GameStates;

public sealed class ServerTickTimingTest
{
    [Test]
    public void ReportsWorstSlowTickAndAggregatesPhasesWithoutLogSpam()
    {
        var log = new Mock<ISawmill>();
        log.Setup(logger => logger.IsLogLevelEnabled(It.IsAny<LogLevel>())).Returns(true);
        var summaries = new List<string>();
        var warnings = new List<string>();
        log.Setup(logger => logger.Info(It.IsAny<string>())).Callback(summaries.Add);
        log.Setup(logger => logger.Warning(It.IsAny<string>())).Callback(warnings.Add);
        var timing = new ServerTickTiming();
        timing.Initialize(log.Object);
        timing.SetEnabled(true);

        RecordTick(1, 0, 4, 10, 50);
        RecordTick(2, 1000, 50, 30, 60);
        RecordTick(3, 2000, 20, 20, 59);
        Assert.That(warnings, Is.Empty);
        Assert.That(summaries, Is.Empty);

        RecordTick(4, 15000, 5, 5, 57);
        RecordTick(5, 30000, 5, 5, 58);
        RecordTick(6, 45000, 5, 5, 58);
        RecordTick(7, 60000, 5, 5, 58);

        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.Contain("tick=2 players=60").And.Contain("over_budget_ticks=2")
            .And.Contain("simulation_ms=50.00").And.Contain("pvs_serialize_ms=30.00")
            .And.Contain("pvs_tick_maintenance_ms=2.00").And.Contain("pvs_dispatch_ms=30.00"));
        Assert.That(summaries, Has.Count.EqualTo(1));
        Assert.That(summaries[0], Does.Contain("ticks=7").And.Contain("players_min=50 players_max=60")
            .And.Contain("async_pvs_ticks=7 over_budget_ticks=2")
            .And.Contain("tick_max_ms=80.00").And.Contain("simulation_max_ms=50.00"));

        timing.SetEnabled(false);
        Assert.That(timing.Enabled, Is.False);
        timing.SetEnabled(true);
        RecordTick(8, 180000, 3, 5, 60);
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(summaries, Has.Count.EqualTo(1));

        void RecordTick(uint tick, int start, int simulation, int pvs, int players)
        {
            timing.BeginTick(Timestamp(start));
            timing.Mark(ServerTickTiming.Phase.Setup, Timestamp(start));
            timing.RecordPvsMaintenance(Timestamp(2));
            timing.Mark(ServerTickTiming.Phase.Simulation, Timestamp(start + simulation));
            timing.Mark(ServerTickTiming.Phase.PostSimulation, Timestamp(start + simulation));
            timing.SetPvsPlayers(players);
            timing.SetAsyncPvs(true);
            timing.Mark(ServerTickTiming.Phase.PvsSerialize, Timestamp(start + simulation + pvs));
            timing.CompleteTick(new GameTick(tick), TimeSpan.FromMilliseconds(33.33), Timestamp(start + simulation + pvs));
            timing.StopTick();
        }

        static long Timestamp(int milliseconds) => milliseconds * Stopwatch.Frequency / 1000;
    }
}
