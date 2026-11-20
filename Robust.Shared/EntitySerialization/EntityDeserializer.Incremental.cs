using System;
using System.Collections.Generic;
using Robust.Shared.GameObjects;

namespace Robust.Shared.EntitySerialization;

public sealed partial class EntityDeserializer
{
    /// <summary>
    /// Allocate all IDs before reading component references, then load one complete entity
    /// per step. Only for pre-mapinit data on a private paused staging map.
    /// </summary>
    internal IEnumerable<GridLoadPhase> CreateEntitySteps()
    {
        if (PostMapInit.Count != 0)
            throw new InvalidOperationException("Incremental grid loading requires pre-mapinit data.");
        foreach (var _ in AllocateEntitySteps(true))
            yield return GridLoadPhase.Allocate;
        if (Options.AssignMapIds) AllocateMapIds();
        foreach (var _ in LoadEntitySteps())
            yield return GridLoadPhase.Components;
        GetRootEntities();
        RemoveEmptyChunks();
        StoreGridTileMap();
        if (Options.AssignMapIds) AssignMapIds();
        CheckCategory();
        yield return GridLoadPhase.Prepare;
    }

    internal IEnumerable<GridLoadPhase> StartEntitySteps()
    {
        AdoptGrids();
        ValidateMapIds();
        BuildEntityHierarchy();
        yield return GridLoadPhase.Hierarchy;
        foreach (var _ in StartupEntitySteps(true))
            yield return GridLoadPhase.Startup;
        SetMapInitLifestage();
        // Pre-mapinit entities already inherited the staging map's pause during startup.
        // Preserve each entity's pause epoch instead of replacing it with the last tick.
        GetRootNodes();
        ProcessDeletions();
        yield return GridLoadPhase.Finalize;
    }
}

public enum GridLoadPhase : byte
{
    Read,
    Prepare,
    Allocate,
    Components,
    Hierarchy,
    Startup,
    MapInit,
    Finalize,
}
