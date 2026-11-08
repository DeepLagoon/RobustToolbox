using System;
using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Dynamics.Contacts;

namespace Robust.Shared.Physics.Systems;

public abstract partial class SharedPhysicsSystem
{
    private readonly HashSet<EntityUid> _fastCollisionSeeds = new();
    private readonly HashSet<EntityUid> _localStepBodies = new();
    private readonly HashSet<EntityUid> _joiningStepBodies = new();
    private readonly HashSet<EntityUid> _localStepParentChanges = new();
    private readonly HashSet<EntityUid> _localStepNeighbors = new();
    private readonly List<EntityUid> _localStepQueue = new();
    private readonly HashSet<EntityUid> _localStepHierarchy = new();
    private readonly Stack<EntityUid> _localStepChildren = new();
    private readonly List<(MapId Map, Box2 Bounds)> _localStepRegions = new();

    private readonly record struct SubstepScope(
        HashSet<EntityUid>? Bodies,
        bool Complement = false,
        HashSet<EntityUid>? AdvancedBodies = null)
    {
        public bool Includes(EntityUid uid) => Bodies == null || Bodies.Contains(uid) != Complement;

        public bool IncludesConstraintBody(EntityUid uid, PhysicsComponent body)
        {
            if (body.BodyType != BodyType.Static)
                return Includes(uid);

            // Static children carried by another scope may already be at a different time.
            return (Complement ? Bodies : AdvancedBodies)?.Contains(uid) != true;
        }

        public bool Includes(Contact contact)
        {
            if (Bodies == null)
                return true;

            var touchesScope = Bodies.Contains(contact.EntityA) || Bodies.Contains(contact.EntityB);
            return touchesScope != Complement &&
                   IncludesConstraintBody(contact.EntityA, contact.BodyA!) &&
                   IncludesConstraintBody(contact.EntityB, contact.BodyB!);
        }
    }

    private void SimulateLocalGridSubsteps(float deltaTime)
    {
        var baseSteps = Math.Max(1, _substeps);
        var interval = deltaTime / baseSteps;
        var maxExtraSteps = Math.Max(1, MaxGridMotionSubsteps / baseSteps);
        var tickStart = _gameTiming.CurTime;

        try
        {
            for (var step = 0; step < baseSteps; step++)
            {
                var start = tickStart + TimeSpan.FromSeconds(step * interval);
                EffectiveCurTime = start;

                // Content controllers apply inputs and forces once per ordinary interval.
                BeforePhysicsStep(false, interval);
                FindPhysicsContacts();

                _fastCollisionSeeds.Clear();
                var substeps = Math.Min(maxExtraSteps, EstimateGridMotionSubsteps(interval, false, 1, _fastCollisionSeeds));
                if (substeps <= 1 || _fastCollisionSeeds.Count == 0)
                {
                    SolvePhysicsStep(false, interval);
                }
                else
                {
                    using (_prof.Group("Local collision scope"))
                        BuildLocalStepScope(interval);

                    RunLocalGridInterval(start, interval, substeps);
                }

                EffectiveCurTime = start;
                AfterPhysicsStep(false, interval);
            }

            FinalStep();
        }
        finally
        {
            EffectiveCurTime = null;
            _fastCollisionSeeds.Clear();
            _localStepBodies.Clear();
            _joiningStepBodies.Clear();
            _localStepParentChanges.Clear();
            _localStepChildren.Clear();
            _localStepQueue.Clear();
            _localStepHierarchy.Clear();
            _localStepNeighbors.Clear();
            _localStepRegions.Clear();
        }
    }

    private void RunLocalGridInterval(TimeSpan start, float interval, int substeps)
    {
        var local = new SubstepScope(_localStepBodies);
        var outside = new SubstepScope(_localStepBodies, Complement: true);
        var frameTime = interval / substeps;

        _transform.OnGlobalMoveEvent += TrackLocalStepParentChange;
        try
        {
            for (var step = 0; step < substeps; step++)
            {
                var elapsed = step * frameTime;
                EffectiveCurTime = start + TimeSpan.FromSeconds(elapsed);
                if (step != 0)
                    FindPhysicsContacts();

                JoinLocalStepBodies(start, elapsed);

                // Only ECS callbacks can add a joint or attach another body during the contact update.
                while (UpdatePhysicsContacts(local) && JoinLocalStepBodies(start, elapsed))
                {
                    // Recompute the new contacts after both sides have reached this time.
                }

                SolvePhysicsIslands(false, frameTime, local, clearForces: false);
            }
        }
        finally
        {
            _transform.OnGlobalMoveEvent -= TrackLocalStepParentChange;
            _localStepParentChanges.Clear();
        }

        _prof.WriteValue("Local physics substeps", substeps);
        _prof.WriteValue("Local step bodies", _localStepBodies.Count);

        // Bodies that never entered the fast zone advance once for the whole interval.
        EffectiveCurTime = start;
        SolvePhysicsStep(false, interval, outside, clearForces: false);

        if (_autoClearForces)
            ClearForces(default);
    }

    private bool JoinLocalStepBodies(TimeSpan start, float elapsed)
    {
        var joined = false;
        while (CollectJoiningStepBodies())
        {
            joined = true;
            using (_prof.Group("Local step catch-up"))
            {
                ExpandStepScope(_joiningStepBodies);

                // Advance only the newly connected component. Contacts and joints with
                // the already advanced zone are evaluated after both sides share a time.
                if (elapsed > 0)
                {
                    EffectiveCurTime = start;
                    var joining = new SubstepScope(_joiningStepBodies, AdvancedBodies: _localStepBodies);
                    SolvePhysicsStep(false, elapsed, joining, clearForces: false);
                    EffectiveCurTime = start + TimeSpan.FromSeconds(elapsed);
                }
            }

            _prof.WriteValue("Local step promoted bodies", _joiningStepBodies.Count);
            _localStepBodies.UnionWith(_joiningStepBodies);
            _joiningStepBodies.Clear();

            // The catch-up may have moved a body or triggered another connection.
            if (elapsed > 0)
                FindPhysicsContacts();
        }

        return joined;
    }

    private void TrackLocalStepParentChange(ref MoveEvent ev)
    {
        if (ev.ParentChanged)
            _localStepParentChanges.Add(ev.Sender);
    }

    private bool CollectJoiningStepBodies()
    {
        foreach (var uid in _localStepParentChanges)
        {
            if (!XformQuery.TryComp(uid, out var xform))
                continue;

            if (_localStepBodies.Contains(xform.ParentUid) ||
                xform.GridUid is { } grid && _localStepBodies.Contains(grid))
            {
                AddStepBody(_joiningStepBodies, uid, carried: true);
            }
        }
        _localStepParentChanges.Clear();

        foreach (var uid in _localStepBodies)
            AddStepConnections(_joiningStepBodies, uid, expandHierarchy: false);

        return _joiningStepBodies.Count != 0;
    }

    private void BuildLocalStepScope(float interval)
    {
        _localStepBodies.Clear();
        _localStepQueue.Clear();
        _localStepHierarchy.Clear();
        _localStepNeighbors.Clear();
        _localStepRegions.Clear();

        // Allow for the full coarse-step displacement of a neighbor that has yet
        // to advance. The predicted hull sweep includes its angular surface motion.
        var neighborTravel = _maxTranslationPerTick + _positionIterations * _maxLinearCorrection;
        foreach (var uid in _fastCollisionSeeds)
        {
            if (!PhysicsQuery.TryComp(uid, out var body) || !XformQuery.TryComp(uid, out var xform))
                continue;

            AddStepBody(_localStepBodies, uid);
            var bounds = _gridQuery.TryComp(uid, out var grid)
                ? GridWorldBounds(grid, xform)
                : GetWorldAABB(uid, body: body, xform: xform);
            var angularTravel = grid == null ? 0f : GridAngularSurfaceSpeed(grid, body) * interval;
            var sweep = bounds.Union(bounds.Translated(body.LinearVelocity * interval))
                .Enlarged(neighborTravel + angularTravel + MaxGridSurfaceTravel);
            if (!float.IsFinite(sweep.Left) || !float.IsFinite(sweep.Right) ||
                !float.IsFinite(sweep.Top) || !float.IsFinite(sweep.Bottom))
                continue;

            // A large hull can nominate many fragments, often in the same area.
            // Merge their overlapping search bounds before querying the trees.
            for (var i = _localStepRegions.Count - 1; i >= 0; i--)
            {
                var (map, other) = _localStepRegions[i];
                if (map != xform.MapID || !sweep.Intersects(other))
                    continue;

                sweep = sweep.Union(other);
                _localStepRegions.RemoveAt(i);
            }

            _localStepRegions.Add((xform.MapID, sweep));
        }

        var maps = EntityManager.System<SharedMapSystem>();
        var neighbors = _localStepNeighbors;
        foreach (var (map, bounds) in _localStepRegions)
        {
            _lookup.GetEntitiesIntersecting(map, bounds, _localStepNeighbors,
                LookupFlags.Dynamic | LookupFlags.Approximate);
            maps.FindGridsIntersecting(map, bounds, ref neighbors,
                static (EntityUid uid, MapGridComponent _, ref HashSet<EntityUid> neighbors) =>
                {
                    neighbors.Add(uid);
                    return true;
                }, approx: true, includeMap: false);
        }

        foreach (var uid in _localStepNeighbors)
            AddStepBody(_localStepBodies, uid);

        ExpandStepScope(_localStepBodies);
    }

    private void ExpandStepScope(HashSet<EntityUid> scope)
    {
        _localStepHierarchy.Clear();
        for (var i = 0; i < _localStepQueue.Count; i++)
            AddStepConnections(scope, _localStepQueue[i], expandHierarchy: true);
        _localStepQueue.Clear();
    }

    private void AddStepConnections(HashSet<EntityUid> scope, EntityUid uid, bool expandHierarchy)
    {
        if (!PhysicsQuery.TryComp(uid, out var body) || !body.CanCollide || !XformQuery.TryComp(uid, out var xform))
            return;

        AddStepBody(scope, xform.ParentUid);
        if (xform.GridUid is { } grid)
            AddStepBody(scope, grid);

        if (expandHierarchy)
        {
            for (var parent = xform.ParentUid; parent != xform.MapUid &&
                 XformQuery.TryComp(parent, out var parentXform); parent = parentXform.ParentUid)
            {
                AddStepBody(scope, parent);
            }

            if (body.BodyType != BodyType.Static)
                AddCarriedStepBodies(scope, xform);
        }

        foreach (var contact in body.Contacts)
        {
            if (contact.Enabled)
                AddStepBody(scope, contact.OtherEnt(uid));
        }

        if (JointQuery.TryComp(uid, out var joints))
            AddJointStepBodies(scope, joints);

        if (RelayTargetQuery.TryComp(uid, out var target))
        {
            foreach (var relayed in target.Relayed)
            {
                if (JointQuery.TryComp(relayed, out joints))
                    AddJointStepBodies(scope, joints);
            }
        }
    }

    private void AddStepBody(HashSet<EntityUid> scope, EntityUid uid, bool carried = false)
    {
        if (_localStepBodies.Contains(uid) || scope != _localStepBodies && scope.Contains(uid))
            return;

        if (PhysicsQuery.TryComp(uid, out var body) && body.CanCollide &&
            (carried || body.BodyType != BodyType.Static) && scope.Add(uid))
        {
            _localStepQueue.Add(uid);
        }
    }

    private void AddCarriedStepBodies(HashSet<EntityUid> scope, TransformComponent root)
    {
        using var children = root.ChildEnumerator;
        while (children.MoveNext(out var child))
            _localStepChildren.Push(child);

        while (_localStepChildren.TryPop(out var child))
        {
            if (!_localStepHierarchy.Add(child) || !XformQuery.TryComp(child, out var xform))
                continue;

            AddStepBody(scope, child, carried: true);
            using var descendants = xform.ChildEnumerator;
            while (descendants.MoveNext(out var descendant))
                _localStepChildren.Push(descendant);
        }
    }

    private EntityUid ResolveStepJointBody(EntityUid uid)
        => JointQuery.TryComp(uid, out var joint) && joint.Relay is { } relay ? relay : uid;

    private void AddJointStepBodies(HashSet<EntityUid> scope, JointComponent component)
    {
        foreach (var joint in component.GetJoints.Values)
        {
            AddStepBody(scope, ResolveStepJointBody(joint.BodyAUid));
            AddStepBody(scope, ResolveStepJointBody(joint.BodyBUid));
        }
    }
}
