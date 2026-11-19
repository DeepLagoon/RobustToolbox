using System;
using System.Collections.Generic;
using Robust.Shared.Log;

namespace Robust.Server;

internal sealed partial class ServerTickTiming
{
    private readonly Dictionary<ushort, PvsComponentTiming> _componentSamples = new();
    private readonly List<KeyValuePair<ushort, PvsComponentTiming>> _componentRanking = new();
    private long _componentCalls, _nullStates, _deltaStates, _unclassifiedStates, _transformStates, _physicsStates;
    private long _componentCandidates, _sampledComponentCalls, _componentCategoriesDropped, _sampledPackets;

    private void RecordPvsPerformance(in PvsSessionTiming timing)
    {
        // Async work is consumed after joining. Configuration changes discard old capture data.
        if (DetailLevel == 0 || !timing.Detailed || timing.Generation != CaptureGeneration) return;
        _componentCalls += timing.ComponentCalls;
        _nullStates += timing.NullStates;
        _deltaStates += timing.DeltaStates;
        _unclassifiedStates += timing.UnclassifiedStates;
        _transformStates += timing.TransformStates;
        _physicsStates += timing.PhysicsStates;
        _componentCandidates += timing.ComponentCandidates;
        _sampledComponentCalls += timing.ComponentSamples;
        if (timing.SampleComponents && !timing.Deferred) _sampledPackets++;
        if (timing.Components == null) return;
        foreach (var (id, sample) in timing.Components)
        {
            if (!_componentSamples.TryGetValue(id, out var stats) && _componentSamples.Count >= 512)
            { _componentCategoriesDropped++; continue; }
            stats.Elapsed += sample.Elapsed;
            stats.Samples += sample.Samples;
            if (sample.Maximum > stats.Maximum) { stats.Maximum = sample.Maximum; stats.WorstEntity = sample.WorstEntity; }
            _componentSamples[id] = stats;
        }
    }

    private void ResetPvsPerformanceWindow()
    {
        _componentSamples.Clear();
        _componentCalls = _nullStates = _deltaStates = _unclassifiedStates = _transformStates = _physicsStates = 0;
        _componentCandidates = _sampledComponentCalls = _componentCategoriesDropped = _sampledPackets = 0;
    }

    private void LogPvsPerformanceDetails()
    {
        _log.Info(FormattableString.Invariant(
            $"Performance PVS coverage: handler_calls={_componentCalls} null_states={_nullStates} delta_states={_deltaStates} unclassified_dirty_states={_unclassifiedStates} transform_calls={_transformStates} physics_calls={_physicsStates} timed_handler_samples={_sampledComponentCalls} candidates_in_sampled_packets={_componentCandidates} sampled_packets={_sampledPackets} sampling_stride=8 max_timed_calls_per_tick=64 categories_dropped={_componentCategoriesDropped} fallback_calls_included=True"));
        _componentRanking.Clear();
        foreach (var entry in _componentSamples) _componentRanking.Add(entry);
        _componentRanking.Sort(static (a, b) => b.Value.Elapsed.CompareTo(a.Value.Elapsed));
        for (var i = 0; i < Math.Min(_detailTop, _componentRanking.Count); i++)
        {
            var (id, sample) = _componentRanking[i];
            var name = _componentFactory != null && _componentFactory.TryGetRegistration(id, out var registration)
                ? registration.Name : "netId:" + id;
            _log.Info(FormattableString.Invariant(
                $"Performance PVS component sample: rank={i + 1} net_id={id} name={name} samples={sample.Samples} sampled_total_ms={sample.Elapsed * MillisecondsPerTimestamp:F3} sampled_avg_ms={sample.Elapsed * MillisecondsPerTimestamp / sample.Samples:F4} sampled_max_ms={sample.Maximum * MillisecondsPerTimestamp:F3} worst_entity_uid={sample.WorstEntity} statistical_estimate=False"));
        }
    }
}
