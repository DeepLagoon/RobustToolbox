using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Robust.Shared.Spawners;

public abstract partial class SharedTimedDespawnSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    private readonly List<Entity<TimedDespawnComponent>> _active = new();
    private readonly List<Entity<TimedDespawnComponent>> _queuedDespawnEntities = new();

    public override void Initialize()
    {
        base.Initialize();
        UpdatesOutsidePrediction = true;
        SubscribeLocalEvent<TimedDespawnComponent, ComponentAdd>(OnAdded);
        SubscribeLocalEvent<TimedDespawnComponent, ComponentRemove>(OnRemoved);
        SubscribeLocalEvent<TimedDespawnComponent, EntityPausedEvent>(OnPaused);
        SubscribeLocalEvent<TimedDespawnComponent, EntityUnpausedEvent>(OnUnpaused);
        var existing = EntityQueryEnumerator<TimedDespawnComponent>();
        while (existing.MoveNext(out var uid, out var component)) AddActive((uid, component));
    }

    private void OnAdded(Entity<TimedDespawnComponent> ent, ref ComponentAdd args)
    {
        if (!IsPaused(ent.Owner)) AddActive(ent);
    }

    private void OnRemoved(Entity<TimedDespawnComponent> ent, ref ComponentRemove args) => RemoveActive(ent.Comp);
    private void OnPaused(Entity<TimedDespawnComponent> ent, ref EntityPausedEvent args) => RemoveActive(ent.Comp);
    private void OnUnpaused(Entity<TimedDespawnComponent> ent, ref EntityUnpausedEvent args) => AddActive(ent);

    private void AddActive(Entity<TimedDespawnComponent> ent)
    {
        if (ent.Comp.ActiveIndex >= 0 || ent.Comp.Deleted) return;
        ent.Comp.ActiveIndex = _active.Count;
        _active.Add(ent);
    }

    private void RemoveActive(TimedDespawnComponent comp)
    {
        var index = comp.ActiveIndex;
        if (index < 0) return;
        var last = _active.Count - 1;
        var replacement = _active[last];
        _active[index] = replacement;
        replacement.Comp.ActiveIndex = index;
        _active.RemoveAt(last);
        comp.ActiveIndex = -1;
    }

    public override void Shutdown()
    {
        foreach (var ent in _active) ent.Comp.ActiveIndex = -1;
        _active.Clear();
        _queuedDespawnEntities.Clear();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // AAAAAAAAAAAAAAAAAAAAAAAAAAA
        // Client both needs to predict this, but also can't properly handle prediction resetting.
        if (!_timing.IsFirstTimePredicted)
            return;

        _queuedDespawnEntities.Clear();

        for (var i = 0; i < _active.Count;)
        {
            var (uid, comp) = _active[i];
            if (comp.Deleted || TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid))
            {
                RemoveActive(comp);
                continue;
            }
            i++;
            comp.Lifetime -= frameTime;

            if (!CanDelete(uid))
                continue;

            if (comp.Lifetime <= 0)
            {
                _queuedDespawnEntities.Add((uid, comp));
            }
        }

        foreach (var queued in _queuedDespawnEntities)
        {
            // Earlier despawn handlers may have removed another queued entity or component.
            if (queued.Comp.Deleted || TerminatingOrDeleted(queued.Owner) || EntityManager.IsQueuedForDeletion(queued.Owner))
                continue;
            var ev = new TimedDespawnEvent();
            RaiseLocalEvent(queued.Owner, ref ev);
            QueueDel(queued.Owner);
        }
    }

    protected abstract bool CanDelete(EntityUid uid);
}
