using System;
using System.Collections.Generic;
using System.Threading;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Robust.Server;

internal sealed partial class ServerTickTiming
{
    private readonly List<string> _reportLines = new(80);
    private int _reportInFlight, _reportEpoch;
    private long _reportsDropped, _reportFailures, _lastReportWriteTicks;

    private void InvalidatePerformanceReports() => Volatile.Write(ref _reportEpoch, CaptureGeneration);

    private bool BeginPerformanceReport()
    {
        // One queued/in-progress report per server. Slow log sinks never grow a backlog.
        if (Interlocked.CompareExchange(ref _reportInFlight, 1, 0) != 0)
        {
            Interlocked.Increment(ref _reportsDropped);
            return false;
        }
        _reportLines.Clear();
        return true;
    }

    private void EmitPerformanceLine(string message) => _reportLines.Add(message);

    private void QueuePerformanceReport()
    {
        var batch = new PerformanceReport(this, CaptureGeneration, _reportLines.ToArray());
        _reportLines.Clear();
        if (!ThreadPool.QueueUserWorkItem(static report => report.Write(), batch, preferLocal: false))
        {
            Interlocked.Increment(ref _reportsDropped);
            Volatile.Write(ref _reportInFlight, 0);
        }
    }

    private void FailPerformanceReport()
    {
        _reportLines.Clear();
        Interlocked.Increment(ref _reportFailures);
        Volatile.Write(ref _reportInFlight, 0);
    }

    private sealed class PerformanceReport(ServerTickTiming owner, int epoch, string[] lines)
    {
        public void Write()
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                foreach (var line in lines)
                {
                    if (Volatile.Read(ref owner._reportEpoch) != epoch) break;
                    owner._log.Info(line);
                }
            }
            catch (Exception)
            {
                // Diagnostics are best-effort. A broken log sink must not crash a worker or the round.
                Interlocked.Increment(ref owner._reportFailures);
            }
            finally
            {
                Interlocked.Exchange(ref owner._lastReportWriteTicks, Stopwatch.GetTimestamp() - started);
                Volatile.Write(ref owner._reportInFlight, 0);
            }
        }
    }
}
