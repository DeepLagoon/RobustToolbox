using System;
using System.Threading.Tasks;
using Prometheus;
using Lidgren.Network;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Robust.Server.GameStates;

internal sealed partial class PvsSystem
{
    [Dependency] private IRobustSerializer _serializer = default!;
    private bool _measurePvs;
    private bool _detailPvs;
    private int _componentSampleSession = -2;
    private int _pvsDiagnosticGeneration;
    private long _replayTiming;

    /// <summary>
    /// Get and serialize <see cref="GameState"/> objects for each player. Compressing & sending the states is done later.
    /// </summary>
    private void SerializeStates()
    {
        using var _ = Histogram.WithLabels("Serialize States").NewTimer();
        var opts = new ParallelOptions {MaxDegreeOfParallelism = _parallelMgr.ParallelProcessCount};
        _oldestAck = GameTick.MaxValue.Value;
        Parallel.For(-1, _sessions.Length, opts, SerializeState);
    }

    /// <summary>
    /// Get and serialize a <see cref="GameState"/> for a single session (or update the current replay).
    /// </summary>
    private void SerializeState(int i)
    {
        try
        {
            var guid = i >= 0 ? _sessions[i].Session.UserId.UserId : default;
            ServerGameStateManager.PvsEventSource.Log.WorkStart(_gameTiming.CurTick.Value, i, guid);

            if (i >= 0)
                SerializeSessionState(_sessions[i], i == _componentSampleSession);
            else
            {
                var start = _measurePvs ? Stopwatch.GetTimestamp() : 0;
                _replay.Update();
                if (_measurePvs)
                    _replayTiming = Stopwatch.GetTimestamp() - start;
            }

            ServerGameStateManager.PvsEventSource.Log.WorkStop(_gameTiming.CurTick.Value, i, guid);
        }
        catch (Exception e) // Catch EVERY exception
        {
            var source = i >= 0 ? _sessions[i].Session.ToString() : "replays";
            Log.Log(LogLevel.Error, e, $"Caught exception while serializing game state for {source}.");
#if !EXCEPTION_TOLERANCE
            throw;
#endif
        }
    }

    /// <summary>
    /// Get and serialize a <see cref="GameState"/> for a single session.
    /// </summary>
    private void SerializeSessionState(PvsSession data, bool sampleComponents = false)
    {
        data.Timing = default;
        if (_measurePvs)
            data.Timing = new PvsSessionTiming
            {
                Tick = _gameTiming.CurTick, Measured = true, Detailed = _detailPvs,
                Generation = _pvsDiagnosticGeneration, SampleComponents = sampleComponents,
            };
        // This session's previous worker result has already been consumed after WaitSendTask.
        data.ComponentTimings?.Clear();
        if (sampleComponents) data.Timing.Components = data.ComponentTimings ??= new();

        // Medium reliable states can also outrun the 64-fragment transport window
        // at high RTT. Build a fresh state when capacity returns instead of queueing
        // a growing replay of obsolete movement. Keep the real ACK as the baseline.
        var transportBlocked = data.Channel is not DummyChannel
                               && !data.Channel.CanSendImmediately(NetDeliveryMethod.ReliableUnordered, 0);
        if (transportBlocked || (data.LastReceivedAck < data.PendingSnapshotTick
                                 && _gameTiming.CurTick < data.SnapshotRetryTick))
        {
            data.Timing.Deferred = true;
            // A deferred receiver must retain its unacknowledged deletion history.
            InterlockedHelper.Min(ref _oldestAck, data.LastReceivedAck.Value);
            data.ClearState();
            data.LastSent = null;
            return;
        }

        var start = _measurePvs ? Stopwatch.GetTimestamp() : 0;
        var state = ComputeSessionState(data);
        if (_measurePvs)
        {
            data.Timing.Build = Stopwatch.GetTimestamp() - start;
            data.Timing.Entities = data.States.Count;
            data.Timing.Chunks = data.Chunks.Count;
            data.Timing.Viewers = data.Viewers.Length;
            data.Timing.Full = data.RequestedFull || data.FromTick == GameTick.Zero;
            data.Timing.AckLag = _gameTiming.CurTick.Value - data.LastReceivedAck.Value;
        }
        InterlockedHelper.Min(ref _oldestAck, data.FromTick.Value);
        DebugTools.AssertEqual(data.StateStream, null);

        // PVS benchmarks use dummy sessions.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (data.Session.Channel is not DummyChannel)
        {
            start = _measurePvs ? Stopwatch.GetTimestamp() : 0;
            data.StateStream = RobustMemoryManager.GetMemoryStream();
            _serializer.SerializeDirect(data.StateStream, state);
            if (_measurePvs)
            {
                data.Timing.Write = Stopwatch.GetTimestamp() - start;
                data.Timing.Bytes = data.StateStream.Length;
            }
        }

        ClearSessionState(data);
    }
}
