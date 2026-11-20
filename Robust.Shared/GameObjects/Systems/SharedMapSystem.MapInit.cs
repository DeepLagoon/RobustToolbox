using System;
using System.Collections.Generic;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;

namespace Robust.Shared.GameObjects;

public abstract partial class SharedMapSystem
{
    public bool IsInitialized(MapId mapId)
    {
        if (mapId == MapId.Nullspace)
            return true; // Nullspace is always initialized

        if(!Maps.TryGetValue(mapId, out var uid))
            throw new ArgumentException($"Map {mapId} does not exist.");

        return IsInitialized(uid);
    }
    public bool IsInitialized(EntityUid? map)
    {
        if (map == null)
            return true; // Nullspace is always initialized

        return IsInitialized(map.Value);
    }

    public bool IsInitialized(Entity<MapComponent?> map)
    {
        if (!_mapQuery.Resolve(map, ref map.Comp))
            return false;

        return map.Comp.MapInitialized;
    }

    public void InitializeMap(MapId mapId, bool unpause = true)
    {
        if(!Maps.TryGetValue(mapId, out var uid))
            throw new ArgumentException($"Map {mapId} does not exist.");

        InitializeMap(uid, unpause);
    }

    public void InitializeMap(Entity<MapComponent?> map, bool unpause = true)
    {
        if (!_mapQuery.Resolve(map, ref map.Comp))
            return;

        if (map.Comp.MapInitialized)
            throw new ArgumentException($"Map {ToPrettyString(map)} is already initialized.");

        RecursiveMapInit(map.Owner);

        if (unpause)
            SetPaused(map, false);
    }

    internal void RecursiveMapInit(EntityUid entity)
    {
        foreach (var _ in MapInitSteps(entity, false)) { }
    }

    /// <summary>Map-init a private paused staging map one complete entity callback at a time.</summary>
    public IEnumerable<EntityUid> InitializeMapSteps(EntityUid map)
    {
        if (!_mapQuery.TryComp(map, out var component) || component.MapInitialized || !IsPaused(map))
            throw new ArgumentException("Expected a paused pre-mapinit staging map.");
        foreach (var uid in MapInitSteps(map, true)) yield return uid;
    }

    private IEnumerable<EntityUid> MapInitSteps(EntityUid entity, bool normalizePause)
    {
        var toInitialize = new List<EntityUid> {entity};
        for (var i = 0; i < toInitialize.Count; i++)
        {
            var uid = toInitialize[i];
            // toInitialize might contain deleted entities.
            if(!_metaQuery.TryComp(uid, out var meta))
                continue;

            if (meta.EntityLifeStage == EntityLifeStage.MapInitialized)
                continue;

            toInitialize.AddRange(Transform(uid)._children);
            // Startup and map-init can happen on different ticks in the opt-in loader.
            // Bring already initialized paused clocks forward before map-init sets fresh ones.
            if (normalizePause && meta.EntityPaused)
            {
                _meta.SetEntityPaused(uid, false, meta);
                _meta.SetEntityPaused(uid, true, meta);
            }
            EntityManager.RunMapInit(uid, meta);
            yield return uid;
        }
    }
}
