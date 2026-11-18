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
    public void WarmDiagnosticSamplingDoesNotAllocatePerTick()
    {
        var timing = new ServerTickTiming();
        timing.Initialize(Mock.Of<ISawmill>());
        timing.SetEnabled(true);
        var budget = TimeSpan.FromMilliseconds(33.33);
        Sample();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 1000; i++)
            Sample();
        var elapsed = Stopwatch.GetElapsedTime(start);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        TestContext.Out.WriteLine($"1000 diagnostic ticks: elapsed_ms={elapsed.TotalMilliseconds:F2} allocated_bytes={bytes}");
        Assert.That(bytes, Is.Zero);

        void Sample()
        {
            timing.BeginTick(0);
            timing.RecordSystem("NPCSystem", 1);
            timing.RecordPvs(new PvsSessionTiming { Measured = true, Build = 1 });
            timing.CompleteTick(new GameTick(1), budget, 1);
            timing.StopTick();
        }
    }

    [Test]
    public void DiagnosticsIncludeTopSystemsCompletedPvsWorkAndGcWithoutPerTickLogging()
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
        var budget = TimeSpan.FromMilliseconds(33.33);

        timing.BeginTick(0);
        timing.RecordSystem("NPCSystem", Timestamp(40));
        timing.RecordSystem("PhysicsSystem", Timestamp(5));
        timing.RecordPvs(new PvsSessionTiming
        {
            Tick = new GameTick(17), Measured = true, Build = Timestamp(20), Write = Timestamp(15),
            Send = Timestamp(5), Entities = 123, Bytes = 60000, WireBytes = 9000, Full = true, AckLag = 60,
        });
        timing.RecordPvs(new PvsSessionTiming { Measured = true, Deferred = true });
        timing.CompleteTick(new GameTick(18), budget, Timestamp(100));
        timing.StopTick();
        Assert.That(warnings, Is.Empty);
        Assert.That(summaries, Is.Empty);
        timing.RecordSystem("OutsideTick", Timestamp(1000));

        timing.BeginTick(Timestamp(15000));
        timing.CompleteTick(new GameTick(19), budget, Timestamp(15001));
        timing.StopTick();
        Assert.That(warnings, Has.Count.EqualTo(2));
        Assert.That(warnings[0], Does.Contain("system1=NPCSystem system1_ms=40.00")
            .And.Contain("system2=PhysicsSystem system2_ms=5.00").And.Contain("gc_pause_ms="));
        Assert.That(warnings[1], Does.Contain("tick=17 build_ms=20.00 write_ms=15.00 send_ms=5.00")
            .And.Contain("entities=123").And.Contain("wire_bytes=9000").And.Contain("ack_lag_ticks=60"));

        timing.BeginTick(Timestamp(60000));
        timing.CompleteTick(new GameTick(20), budget, Timestamp(60001));
        timing.StopTick();
        Assert.That(warnings, Has.Count.EqualTo(2));
        Assert.That(summaries, Has.Count.EqualTo(1));
        Assert.That(summaries[0], Does.Contain("system1=NPCSystem").And.Contain("system1_calls=1")
            .And.Contain("pvs_worker_samples=1 pvs_deferred=1").And.Contain("pvs_state_bytes=60000")
            .And.Contain("gc_gen2=").And.Contain("gc_committed_bytes=").And.Not.Contain("OutsideTick"));
        timing.SetEnabled(false);
        Assert.That(timing.Collecting, Is.False);

        static long Timestamp(int milliseconds) => milliseconds * Stopwatch.Frequency / 1000;
    }

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
