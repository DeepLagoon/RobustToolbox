using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;

namespace Robust.Shared.Physics.Systems;

public abstract partial class SharedPhysicsSystem
{
    // Discrete contact checks must sample relative hull motion well below a tile.
    private const float MaxGridSurfaceTravel = 0.5f;
    private const int MaxGridMotionSubsteps = 64;
    private readonly HashSet<EntityUid> _gridMotionCandidates = new();

    private static float GridSurfaceSpeed(MapGridComponent grid, PhysicsComponent body)
    {
        var speed = body.LinearVelocity.Length() + GridAngularSurfaceSpeed(grid, body);
        return float.IsFinite(speed) ? speed : 0f;
    }

    private static float GridAngularSurfaceSpeed(MapGridComponent grid, PhysicsComponent body)
    {
        var min = Vector2.Abs(grid.LocalAABB.BottomLeft - body.LocalCenter);
        var max = Vector2.Abs(grid.LocalAABB.TopRight - body.LocalCenter);
        var radius = Vector2.Max(min, max).Length();
        return MathF.Abs(body.AngularVelocity) * radius;
    }

    private Box2 GridWorldBounds(MapGridComponent grid, TransformComponent xform)
    {
        var (position, rotation) = _transform.GetWorldPositionRotation(xform);
        return new Box2Rotated(grid.LocalAABB.Translated(position), rotation, position).CalcBoundingBox();
    }

    private int GetGridMotionSubsteps(float deltaTime, bool prediction)
    {
        var baseSteps = Math.Max(1, _substeps);
        var maxGridSpeed = 0f;
        var grids = EntityQueryEnumerator<MapGridComponent, PhysicsComponent>();
        while (grids.MoveNext(out var grid, out var body))
        {
            if (!body.Awake || !body.CanCollide || prediction && !body.Predict)
                continue;
            // Distant parts of a rotating hull can move quickly even with zero linear speed.
            maxGridSpeed = MathF.Max(maxGridSpeed, GridSurfaceSpeed(grid, body));
        }

        if (maxGridSpeed <= 0f || deltaTime <= 0f)
            return baseSteps;

        var maxOtherSpeed = maxGridSpeed;
        foreach (var ent in AwakeBodies)
        {
            if (!ent.Comp1.CanCollide || prediction && !ent.Comp1.Predict)
                continue;
            var speed = ent.Comp1.LinearVelocity.Length();
            if (float.IsFinite(speed))
                maxOtherSpeed = MathF.Max(maxOtherSpeed, speed);
        }

        var travel = (maxGridSpeed + maxOtherSpeed) * deltaTime;
        var required = MathF.Ceiling(travel / MaxGridSurfaceTravel);
        if (required <= baseSteps)
            return baseSteps;

        // The world maximum is only a conservative broadphase search radius. A
        // distant projectile must not select the step count for an unrelated hull.
        var relativeSpeed = GetNearbyGridCollisionSpeed(travel + MaxGridSurfaceTravel, deltaTime, prediction);
        required = MathF.Ceiling(relativeSpeed * deltaTime / MaxGridSurfaceTravel);

        // Preserve configured stepping and bound additional work at extreme admin velocities.
        return Math.Max(baseSteps, (int) Math.Clamp(required, 1f, MaxGridMotionSubsteps));
    }

    private float GetNearbyGridCollisionSpeed(float padding, float deltaTime, bool prediction)
    {
        var maxRelativeSpeed = 0f;
        var maps = EntityManager.System<SharedMapSystem>();
        var grids = EntityQueryEnumerator<MapGridComponent, PhysicsComponent, TransformComponent>();
        while (grids.MoveNext(out var uid, out var grid, out var body, out var xform))
        {
            if (!body.Awake || !body.CanCollide || prediction && !body.Predict || xform.MapUid == null)
                continue;
            var angularSpeed = GridAngularSurfaceSpeed(grid, body);
            var bounds = GridWorldBounds(grid, xform);
            // Work in this grid's translating frame. Sweeping the other bounds
            // by relative velocity excludes receding and parallel-moving bodies.
            // Radius * angular travel conservatively covers rotating hull corners.
            var sweptBounds = bounds.Enlarged(angularSpeed * deltaTime + MaxGridSurfaceTravel);
            var state = (Physics: this, Owner: uid, Bounds: sweptBounds,
                Velocity: body.LinearVelocity, AngularSpeed: angularSpeed,
                DeltaTime: deltaTime, Prediction: prediction, MaxRelative: 0f);
            maps.FindGridsIntersecting(xform.MapID, bounds.Enlarged(padding), ref state,
                static (EntityUid other, MapGridComponent otherGrid,
                    ref (SharedPhysicsSystem Physics, EntityUid Owner, Box2 Bounds, Vector2 Velocity, float AngularSpeed,
                        float DeltaTime, bool Prediction, float MaxRelative) state) =>
                {
                    if (other == state.Owner)
                        return true;
                    var otherVelocity = Vector2.Zero;
                    var otherAngular = 0f;
                    if (state.Physics.PhysicsQuery.TryGetComponent(other, out var otherBody))
                    {
                        if (!otherBody.CanCollide)
                            return true;
                        if (otherBody.Awake && (!state.Prediction || otherBody.Predict))
                        {
                            otherVelocity = otherBody.LinearVelocity;
                            otherAngular = GridAngularSurfaceSpeed(otherGrid, otherBody);
                        }
                    }
                    var relativeVelocity = otherVelocity - state.Velocity;
                    var otherBounds = state.Physics.GridWorldBounds(otherGrid, state.Physics.XformQuery.GetComponent(other));
                    var otherSwept = otherBounds.Union(otherBounds.Translated(relativeVelocity * state.DeltaTime))
                        .Enlarged(otherAngular * state.DeltaTime);
                    if (state.Bounds.Intersects(otherSwept))
                        state.MaxRelative = MathF.Max(state.MaxRelative,
                            relativeVelocity.Length() + state.AngularSpeed + otherAngular);
                    return true;
                }, approx: true, includeMap: false);
            // The map broadphase also contains debris that is not itself a grid. Children
            // of this grid live in its own broadphase, so they do not trigger this query.
            maxRelativeSpeed = MathF.Max(maxRelativeSpeed, state.MaxRelative);
            _gridMotionCandidates.Clear();
            _lookup.GetLocalEntitiesIntersecting(xform.MapUid.Value, bounds.Enlarged(padding), _gridMotionCandidates,
                LookupFlags.Dynamic | LookupFlags.Static | LookupFlags.Approximate);
            foreach (var other in _gridMotionCandidates)
            {
                if (other == uid || HasComp<MapGridComponent>(other) ||
                    !PhysicsQuery.TryGetComponent(other, out var otherBody) || !otherBody.CanCollide)
                    continue;
                var otherVelocity = otherBody.Awake && (!prediction || otherBody.Predict)
                    ? otherBody.LinearVelocity : Vector2.Zero;
                var relativeVelocity = otherVelocity - body.LinearVelocity;
                var relativeSpeed = relativeVelocity.Length() + angularSpeed;
                if (!float.IsFinite(relativeSpeed))
                    continue;
                var otherBounds = _lookup.GetWorldAABB(other);
                var otherSwept = otherBounds.Union(otherBounds.Translated(relativeVelocity * deltaTime));
                if (sweptBounds.Intersects(otherSwept))
                    maxRelativeSpeed = MathF.Max(maxRelativeSpeed, relativeSpeed);
            }
        }
        _gridMotionCandidates.Clear();
        return maxRelativeSpeed;
    }
}
