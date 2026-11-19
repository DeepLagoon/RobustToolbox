using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Robust.Shared.Log;
using Robust.Shared.Timing;

namespace Robust.Server;

internal sealed partial class ServerTickTiming
{
    private sealed class SystemTiming
    {
        public long Tick;
        public long Total;
        public long Maximum;
        public int Samples;
        public int LastSample;
        public long TickMaximum, Allocated, AllocationMaximum;
        public int Failures;
    }

    private readonly Dictionary<string, SystemTiming> _systems = new();
    private readonly (string Name, long Elapsed)[] _slowSystems = new (string, long)[5];
    private readonly (string Name, long Elapsed)[] _topSystems = new (string, long)[5];
    private int _diagnosticSample;
    private long _allocatedStart;
    private TimeSpan _pauseStart;
    private readonly int[] _collectionsStart = new int[3];
    private long _allocatedBaseline;
    private TimeSpan _pauseBaseline;
    private readonly int[] _collectionsBaseline = new int[3];
    private long _tickAllocated;
    private TimeSpan _tickPause;
    private long _slowAllocated;
    private TimeSpan _slowPause;
    private readonly int[] _slowCollections = new int[3];
    private readonly int[] _tickCollections = new int[3];
    private long _pvsSamples;
    private long _pvsDeferred;
    private long _pvsBuild;
    private long _pvsWrite;
    private long _pvsSend;
    private long _pvsReplay;
    private long _pvsBuildMax;
    private long _pvsWriteMax;
    private long _pvsSendMax;
    private long _pvsBytes;
    private long _pvsBytesMax;
    private long _pvsWireBytes;
    private long _pvsWireBytesMax;
    private int _pvsEntitiesMax;
    private PvsSessionTiming _slowPvs;

    public void RecordSystem(string name, long elapsed)
    {
        if (!Collecting)
            return;
        if (!_systems.TryGetValue(name, out var sample))
            _systems.Add(name, sample = new SystemTiming());
        sample.Tick = sample.LastSample == _diagnosticSample ? sample.Tick + elapsed : elapsed;
        sample.TickMaximum = Math.Max(sample.TickMaximum, sample.Tick);
        sample.LastSample = _diagnosticSample;
        sample.Total += elapsed;
        sample.Maximum = Math.Max(sample.Maximum, elapsed);
        sample.Samples++;
    }

    internal void RecordPvs(in PvsSessionTiming sample)
    {
        if (!Collecting)
            return;
        RecordPvsPerformance(in sample);
        if (sample.Deferred)
        {
            _pvsDeferred++;
            return;
        }

        _pvsSamples++;
        _pvsBuild += sample.Build;
        _pvsWrite += sample.Write;
        _pvsSend += sample.Send;
        _pvsBuildMax = Math.Max(_pvsBuildMax, sample.Build);
        _pvsWriteMax = Math.Max(_pvsWriteMax, sample.Write);
        _pvsSendMax = Math.Max(_pvsSendMax, sample.Send);
        _pvsBytes += sample.Bytes;
        _pvsBytesMax = Math.Max(_pvsBytesMax, sample.Bytes);
        _pvsWireBytes += sample.WireBytes;
        _pvsWireBytesMax = Math.Max(_pvsWireBytesMax, sample.WireBytes);
        _pvsEntitiesMax = Math.Max(_pvsEntitiesMax, sample.Entities);
        if (sample.Total > _slowPvs.Total)
            _slowPvs = sample;
    }

    internal void RecordPvsReplay(long elapsed)
    {
        if (Collecting)
            _pvsReplay += elapsed;
    }

    private void BeginDiagnostics()
    {
        _diagnosticSample++;
        _allocatedStart = GC.GetTotalAllocatedBytes(precise: false);
        _pauseStart = GC.GetTotalPauseDuration();
        for (var i = 0; i < _collectionsStart.Length; i++)
            _collectionsStart[i] = GC.CollectionCount(i);
    }

    private void CompleteDiagnostics()
    {
        _tickAllocated = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - _allocatedStart);
        _tickPause = GC.GetTotalPauseDuration() - _pauseStart;
        for (var i = 0; i < _collectionsStart.Length; i++)
            _tickCollections[i] = GC.CollectionCount(i) - _collectionsStart[i];
    }

    private void CaptureSlowDiagnostics()
    {
        Array.Clear(_slowSystems);
        foreach (var (name, sample) in _systems)
        {
            if (sample.LastSample == _diagnosticSample)
                InsertSystem(_slowSystems, name, sample.Tick);
        }
        _slowAllocated = _tickAllocated;
        _slowPause = _tickPause;
        _tickCollections.CopyTo(_slowCollections, 0);
        CaptureSlowPerformanceDetails();
    }

    private static void InsertSystem((string Name, long Elapsed)[] top, string name, long elapsed)
    {
        for (var i = 0; i < top.Length; i++)
        {
            if (elapsed <= top[i].Elapsed)
                continue;
            for (var j = top.Length - 1; j > i; j--)
                top[j] = top[j - 1];
            top[i] = (name, elapsed);
            break;
        }
    }

    private void AppendSlowDiagnostics(StringBuilder msg)
    {
        msg.Append(CultureInfo.InvariantCulture,
            $" gc_allocated_bytes={_slowAllocated} gc_pause_ms={_slowPause.TotalMilliseconds:F2} gc_gen0={_slowCollections[0]} gc_gen1={_slowCollections[1]} gc_gen2={_slowCollections[2]}");
        for (var i = 0; i < _slowSystems.Length && _slowSystems[i].Name != null; i++)
            msg.Append(CultureInfo.InvariantCulture,
                $" system{i + 1}={_slowSystems[i].Name} system{i + 1}_ms={_slowSystems[i].Elapsed * MillisecondsPerTimestamp:F2}");
    }

    private void AppendDiagnosticSummary(StringBuilder msg)
    {
        // Heap information is queried only when formatting the once-per-minute summary.
        var memory = GC.GetGCMemoryInfo();
        msg.Append(CultureInfo.InvariantCulture,
            $" gc_allocated_bytes={Math.Max(0, GC.GetTotalAllocatedBytes(false) - _allocatedBaseline)} gc_pause_ms={(GC.GetTotalPauseDuration() - _pauseBaseline).TotalMilliseconds:F2} gc_gen0={GC.CollectionCount(0) - _collectionsBaseline[0]} gc_gen1={GC.CollectionCount(1) - _collectionsBaseline[1]} gc_gen2={GC.CollectionCount(2) - _collectionsBaseline[2]}");
        msg.Append(CultureInfo.InvariantCulture,
            $" gc_index={memory.Index} gc_heap_bytes={memory.HeapSizeBytes} gc_fragmented_bytes={memory.FragmentedBytes} gc_committed_bytes={memory.TotalCommittedBytes} working_set_bytes={Environment.WorkingSet}");
        msg.Append(CultureInfo.InvariantCulture,
            $" pvs_worker_samples={_pvsSamples} pvs_deferred={_pvsDeferred} pvs_build_worker_ms={_pvsBuild * MillisecondsPerTimestamp:F2}");
        msg.Append(CultureInfo.InvariantCulture,
            $" pvs_write_worker_ms={_pvsWrite * MillisecondsPerTimestamp:F2} pvs_send_worker_ms={_pvsSend * MillisecondsPerTimestamp:F2} pvs_replay_worker_ms={_pvsReplay * MillisecondsPerTimestamp:F2}");
        msg.Append(CultureInfo.InvariantCulture,
            $" pvs_build_session_max_ms={_pvsBuildMax * MillisecondsPerTimestamp:F2} pvs_write_session_max_ms={_pvsWriteMax * MillisecondsPerTimestamp:F2} pvs_send_session_max_ms={_pvsSendMax * MillisecondsPerTimestamp:F2}");
        msg.Append(CultureInfo.InvariantCulture,
            $" pvs_state_bytes={_pvsBytes} pvs_state_bytes_max={_pvsBytesMax} pvs_wire_bytes={_pvsWireBytes} pvs_wire_bytes_max={_pvsWireBytesMax} pvs_entities_max={_pvsEntitiesMax}");
        Array.Clear(_topSystems);
        foreach (var (name, sample) in _systems)
            InsertSystem(_topSystems, name, sample.Total);
        for (var i = 0; i < _topSystems.Length && _topSystems[i].Name != null; i++)
        {
            var (name, total) = _topSystems[i];
            var sample = _systems[name];
            msg.Append(CultureInfo.InvariantCulture,
                $" system{i + 1}={name} system{i + 1}_avg_ms={total * MillisecondsPerTimestamp / _samples:F2} system{i + 1}_max_ms={sample.Maximum * MillisecondsPerTimestamp:F2} system{i + 1}_calls={sample.Samples}");
        }
    }

    private void LogSlowPvs(double budgetMs)
    {
        // Completed async send jobs are sampled after their next join, retaining the originating tick.
        if (_slowPvs.Total * MillisecondsPerTimestamp > budgetMs && _log.IsLogLevelEnabled(LogLevel.Warning))
        {
            _log.Warning(FormattableString.Invariant(
                $"Slow PVS session work: tick={_slowPvs.Tick.Value} build_ms={_slowPvs.Build * MillisecondsPerTimestamp:F2} write_ms={_slowPvs.Write * MillisecondsPerTimestamp:F2} send_ms={_slowPvs.Send * MillisecondsPerTimestamp:F2} entities={_slowPvs.Entities} viewers={_slowPvs.Viewers} chunks={_slowPvs.Chunks} state_bytes={_slowPvs.Bytes} wire_bytes={_slowPvs.WireBytes} full={_slowPvs.Full} ack_lag_ticks={_slowPvs.AckLag}"));
        }
        _slowPvs = default;
    }

    private void ResetDiagnosticSummary()
    {
        foreach (var sample in _systems.Values)
        {
            sample.Total = sample.Maximum = 0;
            sample.Samples = 0;
            sample.TickMaximum = sample.Allocated = sample.AllocationMaximum = 0;
            sample.Failures = 0;
        }
        _allocatedBaseline = GC.GetTotalAllocatedBytes(precise: false);
        _pauseBaseline = GC.GetTotalPauseDuration();
        for (var i = 0; i < _collectionsBaseline.Length; i++)
            _collectionsBaseline[i] = GC.CollectionCount(i);
        _pvsSamples = _pvsDeferred = _pvsBuild = _pvsWrite = _pvsSend = _pvsReplay = 0;
        _pvsBuildMax = _pvsWriteMax = _pvsSendMax = _pvsBytes = _pvsBytesMax = 0;
        _pvsWireBytes = _pvsWireBytesMax = 0;
        _pvsEntitiesMax = 0;
        ResetPerformanceWindow();
    }

    private void ResetDiagnostics()
    {
        _systems.Clear();
        _diagnosticSample = 0;
        _slowPvs = default;
        _detailTimings.Clear();
        Array.Clear(_slowSystems);
        ResetDiagnosticSummary();
    }
}

/// <summary>One session's completed worker measurements, consumed only after all PVS jobs have joined.</summary>
internal struct PvsSessionTiming
{
    public int Generation;
    public bool Detailed, SampleComponents;
    public int ComponentCalls, NullStates, DeltaStates, UnclassifiedStates, TransformStates, PhysicsStates;
    public int ComponentCandidates, ComponentSamples;
    public Dictionary<ushort, PvsComponentTiming>? Components;
    public GameTick Tick;
    public bool Measured;
    public bool Deferred;
    public long Build;
    public long Write;
    public long Send;
    public int Entities;
    public int Viewers;
    public int Chunks;
    public long Bytes;
    public long WireBytes;
    public bool Full;
    public uint AckLag;
    public readonly long Total => Build + Write + Send;
}

internal struct PvsComponentTiming
{
    public long Elapsed, Maximum, Samples;
    public Robust.Shared.GameObjects.EntityUid WorstEntity;
}
