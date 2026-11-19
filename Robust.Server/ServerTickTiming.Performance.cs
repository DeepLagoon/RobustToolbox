using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Robust.Server;

internal sealed partial class ServerTickTiming
{
    private sealed class DetailTiming
    {
        public long Total, Maximum, Tick, Items, ItemsMaximum, Calls;
        public int LastSample;
    }

    private readonly Dictionary<string, DetailTiming> _detailTimings = new();
    private readonly List<KeyValuePair<string, SystemTiming>> _systemRanking = new();
    private readonly List<KeyValuePair<string, DetailTiming>> _detailRanking = new();
    private readonly (string Name, long Elapsed)[] _slowDetails = new (string, long)[8];
    private static readonly double[] TickBuckets =
        [1, 2, 4, 6, 8, 10, 12, 16, 20, 25, 30, 33.34, 40, 50, 66.67, 80, 100, 150, 200, 300, 500, 1000, 2000, double.PositiveInfinity];
    private readonly long[] _tickHistogram = new long[TickBuckets.Length];
    private long _detailSummaryInterval = 30 * Stopwatch.Frequency;
    private long _detailSlowInterval = 10 * Stopwatch.Frequency;
    private int _detailTop = 20;
    private long _detailDropped, _previousReportTicks;
    private TimeSpan _processCpuBaseline;
    private Func<string>? _context;

    public int DetailLevel { get; private set; }
    public bool DetailedCollecting => Collecting && DetailLevel >= 2;

    public void ConfigurePerformance(int level, float interval, float slowInterval, int top)
    {
        DetailLevel = Math.Clamp(level, 0, 2);
        CaptureGeneration++;
        InvalidatePerformanceReports();
        _detailSummaryInterval = (long)((float.IsFinite(interval) ? Math.Clamp(interval, 5, 600) : 30) * Stopwatch.Frequency);
        _detailSlowInterval = (long)((float.IsFinite(slowInterval) ? Math.Clamp(slowInterval, 1, 120) : 10) * Stopwatch.Frequency);
        _detailTop = Math.Clamp(top, 1, 100);
        Enabled = _basicEnabled || DetailLevel > 0;
        Collecting = false;
        _reset = true;
    }

    public void SetContextSource(Func<string> source) => _context = source;

    public void RecordSystemAllocation(string name, long allocated, bool failed)
    {
        if (!DetailedCollecting || !_systems.TryGetValue(name, out var sample)) return;
        sample.Allocated += allocated;
        sample.AllocationMaximum = Math.Max(sample.AllocationMaximum, allocated);
        if (failed) sample.Failures++;
    }

    public void RecordDetail(string name, long elapsed, int items)
    {
        if (!Collecting || DetailLevel == 0) return;
        if (!_detailTimings.TryGetValue(name, out var sample))
        {
            if (_detailTimings.Count >= 256) { _detailDropped++; return; }
            _detailTimings.Add(name, sample = new DetailTiming());
        }
        sample.Tick = sample.LastSample == _diagnosticSample ? sample.Tick + elapsed : elapsed;
        sample.LastSample = _diagnosticSample;
        sample.Total += elapsed;
        sample.Maximum = Math.Max(sample.Maximum, elapsed);
        sample.Items += items;
        sample.ItemsMaximum = Math.Max(sample.ItemsMaximum, items);
        sample.Calls++;
    }

    private void RecordPerformanceTick(long elapsed)
    {
        if (DetailLevel == 0) return;
        var ms = elapsed * MillisecondsPerTimestamp;
        for (var i = 0; i < TickBuckets.Length; i++)
            if (ms <= TickBuckets[i]) { _tickHistogram[i]++; break; }
    }

    private double TickPercentile(double fraction)
    {
        var target = Math.Max(1L, (long)Math.Ceiling(_samples * fraction));
        long count = 0;
        for (var i = 0; i < TickBuckets.Length; i++)
            if ((count += _tickHistogram[i]) >= target) return TickBuckets[i];
        return 0;
    }

    private void CaptureSlowPerformanceDetails()
    {
        if (DetailLevel == 0) return;
        Array.Clear(_slowDetails);
        foreach (var (name, sample) in _detailTimings)
            if (sample.LastSample == _diagnosticSample) InsertSystem(_slowDetails, name, sample.Tick);
    }

    private void AppendSlowPerformanceDetails(StringBuilder msg)
    {
        if (DetailLevel == 0) return;
        for (var i = 0; i < _slowDetails.Length && _slowDetails[i].Name != null; i++)
            msg.Append(CultureInfo.InvariantCulture, $" detail{i + 1}={_slowDetails[i].Name} detail{i + 1}_ms={_slowDetails[i].Elapsed * MillisecondsPerTimestamp:F3}");
    }

    private void ResetPerformanceWindow()
    {
        Array.Clear(_tickHistogram);
        _detailDropped = 0;
        foreach (var sample in _detailTimings.Values)
        {
            sample.Total = sample.Maximum = sample.Items = sample.ItemsMaximum = sample.Calls = 0;
        }
        if (DetailLevel > 0)
        {
            using var process = Process.GetCurrentProcess();
            _processCpuBaseline = process.TotalProcessorTime;
        }
        ResetPvsPerformanceWindow();
    }

    private void LogPerformanceDetails(long now)
    {
        if (DetailLevel == 0 || _samples == 0 || !BeginPerformanceReport()) return;
        var started = Stopwatch.GetTimestamp();
        try
        {
        using var process = Process.GetCurrentProcess();
        var cpuMs = Math.Max(0, (process.TotalProcessorTime - _processCpuBaseline).TotalMilliseconds);
        var wallMs = (now - _summaryStart) * MillisecondsPerTimestamp;
        var msg = new StringBuilder(512);
        msg.Append(CultureInfo.InvariantCulture,
            $"Performance context: level={DetailLevel} capture_generation={CaptureGeneration} ticks={_samples} p50_upper_bucket_ms={TickPercentile(0.5):F2} p95_upper_bucket_ms={TickPercentile(0.95):F2} p99_upper_bucket_ms={TickPercentile(0.99):F2} process_cpu_ms={cpuMs:F2} process_cpu_cores={cpuMs / Math.Max(1, wallMs):F2} logical_processors={Environment.ProcessorCount} thread_pool_threads={System.Threading.ThreadPool.ThreadCount} thread_pool_pending={System.Threading.ThreadPool.PendingWorkItemCount} previous_report_build_ms={_previousReportTicks * MillisecondsPerTimestamp:F3} previous_report_write_ms={System.Threading.Interlocked.Read(ref _lastReportWriteTicks) * MillisecondsPerTimestamp:F3} reports_dropped={System.Threading.Interlocked.Read(ref _reportsDropped)} reporter_failures={System.Threading.Interlocked.Read(ref _reportFailures)} detail_categories_dropped={_detailDropped}");
        if (_context != null) msg.Append(' ').Append(_context());
        EmitPerformanceLine(msg.ToString());

        _systemRanking.Clear();
        foreach (var entry in _systems) if (entry.Value.Samples > 0) _systemRanking.Add(entry);
        _systemRanking.Sort(static (a, b) => b.Value.Total.CompareTo(a.Value.Total));
        long shown = 0, all = 0;
        foreach (var entry in _systemRanking) all += entry.Value.Total;
        for (var i = 0; i < Math.Min(_detailTop, _systemRanking.Count); i++)
        {
            var (name, sample) = _systemRanking[i];
            shown += sample.Total;
            EmitPerformanceLine(FormattableString.Invariant(
                $"Performance system: rank={i + 1} name={name} avg_ms_per_tick={sample.Total * MillisecondsPerTimestamp / _samples:F3} max_call_ms={sample.Maximum * MillisecondsPerTimestamp:F3} max_tick_ms={sample.TickMaximum * MillisecondsPerTimestamp:F3} calls={sample.Samples} allocated_main_thread_bytes={sample.Allocated} max_call_allocated_main_thread_bytes={sample.AllocationMaximum} failed_calls={sample.Failures} allocations_measured={DetailLevel >= 2}"));
        }
        EmitPerformanceLine(FormattableString.Invariant($"Performance system coverage: systems={_systemRanking.Count} shown={Math.Min(_detailTop, _systemRanking.Count)} other_avg_ms_per_tick={(all - shown) * MillisecondsPerTimestamp / _samples:F3} nested_engine_entries=True"));
        _detailRanking.Clear();
        foreach (var entry in _detailTimings) if (entry.Value.Calls > 0) _detailRanking.Add(entry);
        _detailRanking.Sort(static (a, b) => b.Value.Total.CompareTo(a.Value.Total));
        for (var i = 0; i < Math.Min(_detailTop, _detailRanking.Count); i++)
        {
            var (name, sample) = _detailRanking[i];
            EmitPerformanceLine(FormattableString.Invariant(
                $"Performance phase: name={name} avg_ms_per_tick={sample.Total * MillisecondsPerTimestamp / _samples:F3} max_call_ms={sample.Maximum * MillisecondsPerTimestamp:F3} calls={sample.Calls} items_total={sample.Items} items_max={sample.ItemsMaximum} nested=True"));
        }
        var counters = 0;
        foreach (var (name, sample) in _detailRanking)
        {
            if (sample.ItemsMaximum == 0 || counters++ >= 16) continue;
            EmitPerformanceLine(FormattableString.Invariant($"Performance work counter: name={name} calls={sample.Calls} items_total={sample.Items} items_max={sample.ItemsMaximum}"));
        }
        EmitPerformanceLine(FormattableString.Invariant($"Performance phase coverage: categories={_detailRanking.Count} shown={Math.Min(_detailTop, _detailRanking.Count)} nesting_additive=False"));
        LogPvsPerformanceDetails();
        EmitPerformanceLine(FormattableString.Invariant($"Performance report end: capture_generation={CaptureGeneration} ticks={_samples}"));
        QueuePerformanceReport();
        }
        catch (Exception)
        {
            FailPerformanceReport();
        }
        finally
        {
        _previousReportTicks = Stopwatch.GetTimestamp() - started;
        }
    }
}
