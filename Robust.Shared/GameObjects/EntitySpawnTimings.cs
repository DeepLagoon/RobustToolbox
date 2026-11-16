using System;
using System.Diagnostics;

namespace Robust.Shared.GameObjects;

/// <summary>
/// Aggregates the cost of explicitly timed entity spawns.
/// </summary>
public sealed class EntitySpawnTimings
{
    private long _createTicks;
    private long _initializeTicks;

    public int Count { get; private set; }
    public TimeSpan Create => Stopwatch.GetElapsedTime(0, _createTicks);
    public TimeSpan Initialize => Stopwatch.GetElapsedTime(0, _initializeTicks);

    internal void Add(long start, long created, long initialized)
    {
        _createTicks += created - start;
        _initializeTicks += initialized - created;
        Count++;
    }
}
