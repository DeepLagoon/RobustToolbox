using System.Collections.Concurrent;
using System.Threading;
using Moq;
using NUnit.Framework;
using Robust.Server;
using Robust.Shared.Log;
using Robust.Shared.Timing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Robust.UnitTesting.Server.GameStates;

public sealed class ServerPerformanceLoggingTest
{
    [Test]
    public void DetailModeIsIndependentFromBasicLoggingAndSwitchesOffImmediately()
    {
        var timing = new ServerTickTiming();
        timing.Initialize(Mock.Of<ISawmill>());
        timing.SetEnabled(false);
        timing.ConfigurePerformance(2, 30, 10, 20);
        Assert.That(timing.Enabled, Is.True);
        timing.BeginTick(0);
        Assert.That(timing.DetailedCollecting, Is.True);
        timing.ConfigurePerformance(0, 30, 10, 20);
        Assert.Multiple(() =>
        {
            Assert.That(timing.Enabled, Is.False);
            Assert.That(timing.Collecting, Is.False);
        });
        timing.SetEnabled(true);
        timing.ConfigurePerformance(2, 30, 10, 20);
        timing.ConfigurePerformance(0, 30, 10, 20);
        Assert.That(timing.Enabled, Is.True, "Disabling additional diagnostics preserves the independent baseline setting.");
    }

    [Test]
    public void WarmExtendedAggregationDoesNotAllocateEachTick()
    {
        var timing = new ServerTickTiming();
        timing.Initialize(Mock.Of<ISawmill>());
        timing.ConfigurePerformance(2, 30, 10, 20);
        var packet = new PvsSessionTiming
        {
            Measured = true, Detailed = true, Generation = timing.CaptureGeneration,
            ComponentCalls = 3, ComponentSamples = 1, ComponentCandidates = 8,
            Components = new() { [1] = new PvsComponentTiming { Elapsed = 1, Maximum = 1, Samples = 1 } },
        };
        Sample();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) Sample();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);

        void Sample()
        {
            timing.BeginTick(0);
            timing.RecordSystem("PhysicsSystem", 1);
            timing.RecordSystemAllocation("PhysicsSystem", 10, false);
            timing.RecordDetail("physics.collide", 1, 20);
            timing.RecordPvs(in packet);
            timing.CompleteTick(new GameTick(1), TimeSpan.FromMilliseconds(33.33), 1);
            timing.StopTick();
        }
    }

    [Test]
    public void ReportRanksSystemsLimitsRowsAndRejectsOldPvsCapture()
    {
        var log = new Mock<ISawmill>();
        log.Setup(l => l.IsLogLevelEnabled(It.IsAny<LogLevel>())).Returns(true);
        var messages = new ConcurrentQueue<string>();
        using var finished = new ManualResetEventSlim();
        log.Setup(l => l.Info(It.IsAny<string>())).Callback<string>(message =>
        {
            messages.Enqueue(message);
            if (message.StartsWith("Performance report end:")) finished.Set();
        });
        var timing = new ServerTickTiming();
        timing.Initialize(log.Object);
        timing.ConfigurePerformance(2, 5, 10, 2);
        timing.BeginTick(0);
        timing.RecordSystem("Hot", Stamp(20));
        timing.RecordSystemAllocation("Hot", 1000, true);
        timing.RecordSystem("Second", Stamp(3));
        timing.RecordSystem("Tail", Stamp(1));
        timing.RecordDetail("physics.collide", Stamp(2), 100);
        timing.RecordPvs(new PvsSessionTiming
        {
            Measured = true, Detailed = true, Generation = timing.CaptureGeneration - 1, ComponentCalls = 999,
        });
        timing.RecordPvs(new PvsSessionTiming
        {
            Measured = true, Detailed = true, Generation = timing.CaptureGeneration, ComponentCalls = 7,
        });
        timing.CompleteTick(new GameTick(1), TimeSpan.FromMilliseconds(33.33), Stamp(39));
        timing.StopTick();
        timing.RecordSystem("OutsideTick", Stamp(999));
        timing.BeginTick(Stamp(5000));
        timing.CompleteTick(new GameTick(2), TimeSpan.FromMilliseconds(33.33), Stamp(5001));
        timing.StopTick();
        Assert.That(finished.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(SpinWait.SpinUntil(() => !timing.PerformanceReportPending, TimeSpan.FromSeconds(10)), Is.True);
        var rows = messages.ToArray();
        var systems = rows.Where(x => x.StartsWith("Performance system:")).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(systems, Has.Length.EqualTo(2));
            Assert.That(systems[0], Does.Contain("rank=1 name=Hot").And.Contain("allocated_main_thread_bytes=1000").And.Contain("failed_calls=1"));
            Assert.That(rows.Any(x => x.Contains("p95_upper_bucket_ms=40.00")), Is.True);
            Assert.That(rows.Any(x => x.StartsWith("Performance PVS coverage: handler_calls=7")), Is.True);
            Assert.That(rows.Any(x => x.Contains("name=OutsideTick")), Is.False);
        });
    }

    [Test]
    public void BlockedLogSinkDoesNotBlockTickOrCreateAReportBacklogAndDisableCancelsRest()
    {
        var log = new Mock<ISawmill>();
        log.Setup(l => l.IsLogLevelEnabled(It.IsAny<LogLevel>())).Returns(true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var messages = new ConcurrentQueue<string>();
        log.Setup(l => l.Info(It.IsAny<string>())).Callback<string>(message =>
        {
            messages.Enqueue(message);
            if (!message.StartsWith("Server tick timing summary:")) return;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        var timing = new ServerTickTiming();
        timing.Initialize(log.Object);
        timing.SetEnabled(false);
        timing.ConfigurePerformance(2, 5, 10, 2);
        try
        {
            Tick(0, 1);
            Tick(5000, 2);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            var started = Stopwatch.GetTimestamp();
            Tick(11000, 3);
            Assert.That(Stopwatch.GetElapsedTime(started), Is.LessThan(TimeSpan.FromSeconds(2)));
            timing.ConfigurePerformance(0, 5, 10, 2);
            release.Set();
            Assert.That(SpinWait.SpinUntil(() => !timing.PerformanceReportPending, TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(messages.Any(x => x.StartsWith("Performance system:")), Is.False);
            Assert.That(messages.Count(x => x.StartsWith("Server tick timing summary:")), Is.EqualTo(1));
            Assert.That(messages.Any(x => x.StartsWith("Performance context:")), Is.False);
        }
        finally
        {
            release.Set();
            SpinWait.SpinUntil(() => !timing.PerformanceReportPending, TimeSpan.FromSeconds(10));
        }

        void Tick(int start, uint tick)
        {
            timing.BeginTick(Stamp(start));
            timing.RecordSystem("NPCSystem", 1);
            timing.CompleteTick(new GameTick(tick), TimeSpan.FromMilliseconds(33.33), Stamp(start + 1));
            timing.StopTick();
        }
    }

    private static long Stamp(int ms) => ms * Stopwatch.Frequency / 1000;
}
