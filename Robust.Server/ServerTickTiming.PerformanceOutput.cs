using System;
using System.Collections.Generic;
using System.Threading;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Robust.Server;

internal sealed partial class ServerTickTiming
{
    private readonly List<string> _reportLines = new(80);
    private readonly List<string> _warningLines = new(2);
    private int _reportInFlight, _warningInFlight, _reportEpoch;
    private string? _performanceSummaryLine;
    private long _reportsDropped, _reportFailures, _lastReportWriteTicks;
    internal bool PerformanceReportPending => Volatile.Read(ref _reportInFlight) != 0 || Volatile.Read(ref _warningInFlight) != 0;

    private void InvalidatePerformanceReports()
    {
        Volatile.Write(ref _reportEpoch, CaptureGeneration);
        _warningLines.Clear();
        _performanceSummaryLine = null;
    }

    private bool BeginPerformanceReport()
    {
        // One queued/in-progress report per server. Slow log sinks never grow a backlog.
        if (Interlocked.CompareExchange(ref _reportInFlight, 1, 0) != 0)
        {
            Interlocked.Increment(ref _reportsDropped);
            _performanceSummaryLine = null;
            return false;
        }
        _reportLines.Clear();
        if (_performanceSummaryLine != null) _reportLines.Add(_performanceSummaryLine);
        _performanceSummaryLine = null;
        return true;
    }

    private void EmitPerformanceLine(string message) => _reportLines.Add(message);

    private void QueuePerformanceReport()
    {
        var batch = new PerformanceReport(this, CaptureGeneration, _reportLines.ToArray(), false);
        _reportLines.Clear();
        if (!ThreadPool.QueueUserWorkItem(static report => report.Write(), batch, preferLocal: false))
        {
            Interlocked.Increment(ref _reportsDropped);
            Volatile.Write(ref _reportInFlight, 0);
        }
    }

    private void QueuePerformanceWarning(string message)
    {
        if (_warningLines.Count < 2) _warningLines.Add(message);
        else Interlocked.Increment(ref _reportsDropped);
    }

    private void FlushPerformanceWarnings()
    {
        if (_warningLines.Count == 0) return;
        if (Interlocked.CompareExchange(ref _warningInFlight, 1, 0) != 0)
        { Interlocked.Increment(ref _reportsDropped); _warningLines.Clear(); return; }
        try
        {
            var batch = new PerformanceReport(this, CaptureGeneration, _warningLines.ToArray(), true);
            _warningLines.Clear();
            if (ThreadPool.QueueUserWorkItem(static report => report.Write(), batch, preferLocal: false)) return;
            Interlocked.Increment(ref _reportsDropped);
        }
        catch (Exception) { Interlocked.Increment(ref _reportFailures); }
        _warningLines.Clear();
        Volatile.Write(ref _warningInFlight, 0);
    }

    private void FailPerformanceReport()
    {
        _reportLines.Clear();
        Interlocked.Increment(ref _reportFailures);
        Volatile.Write(ref _reportInFlight, 0);
    }

    private sealed class PerformanceReport(ServerTickTiming owner, int epoch, string[] lines, bool warning)
    {
        public void Write()
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                foreach (var line in lines)
                {
                    if (Volatile.Read(ref owner._reportEpoch) != epoch) break;
                    if (warning) owner._log.Warning(line);
                    else owner._log.Info(line);
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
                if (warning) Volatile.Write(ref owner._warningInFlight, 0);
                else Volatile.Write(ref owner._reportInFlight, 0);
            }
        }
    }
}
