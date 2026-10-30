using System;
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
            var min = Vector2.Abs(grid.LocalAABB.BottomLeft - body.LocalCenter);
            var max = Vector2.Abs(grid.LocalAABB.TopRight - body.LocalCenter);
            var radius = Vector2.Max(min, max).Length();
            var speed = body.LinearVelocity.Length() + MathF.Abs(body.AngularVelocity) * radius;
            if (float.IsFinite(speed))
                maxGridSpeed = MathF.Max(maxGridSpeed, speed);
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
        if (required <= baseSteps || !HasPotentialGridCollision(travel + MaxGridSurfaceTravel, prediction))
            return baseSteps;

        // Preserve configured stepping and bound additional work at extreme admin velocities.
        return Math.Max(baseSteps, (int) Math.Clamp(required, 1f, MaxGridMotionSubsteps));
    }

    private bool HasPotentialGridCollision(float padding, bool prediction)
    {
        var maps = EntityManager.System<SharedMapSystem>();
        var grids = EntityQueryEnumerator<MapGridComponent, PhysicsComponent, TransformComponent>();
        while (grids.MoveNext(out var uid, out var grid, out var body, out var xform))
        {
            if (!body.Awake || !body.CanCollide || prediction && !body.Predict || xform.MapUid == null)
                continue;
            var (position, rotation) = _transform.GetWorldPositionRotation(xform);
            var bounds = new Box2Rotated(grid.LocalAABB.Translated(position), rotation, position)
                .CalcBoundingBox().Enlarged(padding);
            var state = (Owner: uid, Found: false);
            maps.FindGridsIntersecting(xform.MapID, bounds, ref state,
                static (EntityUid other, MapGridComponent _, ref (EntityUid Owner, bool Found) state) =>
                {
                    if (other == state.Owner)
                        return true;
                    state.Found = true;
                    return false;
                }, approx: true, includeMap: false);
            // The map broadphase also contains debris that is not itself a grid. Children
            // of this grid live in its own broadphase, so they do not trigger this query.
            if (state.Found || _lookup.AnyLocalEntitiesIntersecting(xform.MapUid.Value, bounds,
                    LookupFlags.Dynamic | LookupFlags.Static | LookupFlags.Approximate, ignored: uid))
                return true;
        }
        return false;
    }
}
