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
        => EstimateGridMotionSubsteps(deltaTime, prediction, Math.Max(1, _substeps));

    private int EstimateGridMotionSubsteps(float deltaTime, bool prediction, int baseSteps,
        HashSet<EntityUid>? seeds = null)
    {
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

        var maxOtherSpeedSquared = 0f;
        foreach (var ent in AwakeBodies)
        {
            if (!ent.Comp1.CanCollide || prediction && !ent.Comp1.Predict)
                continue;
            var speedSquared = ent.Comp1.LinearVelocity.LengthSquared();
            if (float.IsFinite(speedSquared))
                maxOtherSpeedSquared = MathF.Max(maxOtherSpeedSquared, speedSquared);
        }

        var maxOtherSpeed = MathF.Max(maxGridSpeed, MathF.Sqrt(maxOtherSpeedSquared));
        var travel = (maxGridSpeed + maxOtherSpeed) * deltaTime;
        var required = MathF.Ceiling(travel / MaxGridSurfaceTravel);
        if (required <= baseSteps)
            return baseSteps;

        // The world maximum is only a conservative broadphase search radius. A
        // distant projectile must not select the step count for an unrelated hull.
        var minRelativeSpeed = baseSteps * MaxGridSurfaceTravel / deltaTime;
        var relativeSpeed = GetNearbyGridCollisionSpeed(travel + MaxGridSurfaceTravel, deltaTime, prediction,
            minRelativeSpeed, seeds);
        required = MathF.Ceiling(relativeSpeed * deltaTime / MaxGridSurfaceTravel);

        // Preserve configured stepping and bound additional work at extreme admin velocities.
        return Math.Max(baseSteps, (int) Math.Clamp(required, 1f, MaxGridMotionSubsteps));
    }

    private float GetNearbyGridCollisionSpeed(float padding, float deltaTime, bool prediction,
        float minRelativeSpeed, HashSet<EntityUid>? seeds)
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
                DeltaTime: deltaTime, Prediction: prediction, MinRelative: minRelativeSpeed,
                Seeds: seeds, MaxRelative: 0f);
            maps.FindGridsIntersecting(xform.MapID, bounds.Enlarged(padding), ref state,
                static (EntityUid other, MapGridComponent otherGrid,
                    ref (SharedPhysicsSystem Physics, EntityUid Owner, Box2 Bounds, Vector2 Velocity, float AngularSpeed,
                        float DeltaTime, bool Prediction, float MinRelative, HashSet<EntityUid>? Seeds,
                        float MaxRelative) state) =>
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
                    var relativeSpeed = relativeVelocity.Length() + state.AngularSpeed + otherAngular;
                    if (!float.IsFinite(relativeSpeed) || relativeSpeed <= (state.Seeds == null ? state.MaxRelative : state.MinRelative))
                        return true;

                    var otherBounds = state.Physics.GridWorldBounds(otherGrid, state.Physics.XformQuery.GetComponent(other));
                    if (SweepsIntoBounds(state.Bounds, otherBounds.Enlarged(otherAngular * state.DeltaTime),
                        relativeVelocity * state.DeltaTime))
                    {
                        state.MaxRelative = MathF.Max(state.MaxRelative, relativeSpeed);
                        state.Seeds?.Add(state.Owner);
                        state.Seeds?.Add(other);
                    }
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
                if (!float.IsFinite(relativeSpeed) || relativeSpeed <= (seeds == null ? maxRelativeSpeed : minRelativeSpeed))
                    continue;
                var otherBounds = _lookup.GetWorldAABB(other);
                if (SweepsIntoBounds(sweptBounds, otherBounds, relativeVelocity * deltaTime))
                {
                    maxRelativeSpeed = MathF.Max(maxRelativeSpeed, relativeSpeed);
                    seeds?.Add(uid);
                    seeds?.Add(other);
                }
            }
        }
        _gridMotionCandidates.Clear();
        return maxRelativeSpeed;
    }

    // The union of the start and end boxes contains empty corners on a diagonal path.
    // Both axes must overlap at the same time for the pair to request smaller steps.
    private static bool SweepsIntoBounds(Box2 bounds, Box2 moving, Vector2 displacement)
    {
        var start = 0f;
        var end = 1f;
        return OverlapsDuringSweep(bounds.Left - moving.Right, bounds.Right - moving.Left, displacement.X, ref start, ref end)
            && OverlapsDuringSweep(bounds.Bottom - moving.Top, bounds.Top - moving.Bottom, displacement.Y, ref start, ref end);
    }

    private static bool OverlapsDuringSweep(float min, float max, float displacement, ref float start, ref float end)
    {
        if (displacement == 0f)
            return min <= 0f && max >= 0f;

        var entry = min / displacement;
        var exit = max / displacement;
        start = MathF.Max(start, MathF.Min(entry, exit));
        end = MathF.Min(end, MathF.Max(entry, exit));
        return start <= end;
    }
}
