using System;

namespace Robust.Shared.EntitySerialization;

/// <summary>
/// Elapsed times for a successful map load. Initialization includes entity startup and map initialization.
/// </summary>
public readonly record struct MapLoadTimings(
    TimeSpan Elapsed,
    TimeSpan ReadYaml,
    TimeSpan Prepare,
    TimeSpan CreateEntities,
    TimeSpan InitializeEntities,
    int EntityCount);
