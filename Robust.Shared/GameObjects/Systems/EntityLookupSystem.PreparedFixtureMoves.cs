using System.Numerics;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;

namespace Robust.Shared.GameObjects;

public sealed partial class EntityLookupSystem
{
    internal readonly record struct PreparedFixtureMove(
        EntityUid Owner,
        EntityUid Grid,
        Vector2 Position,
        Angle Rotation,
        Box2 Bounds,
        float Radius,
        Vector2 Point0,
        Vector2 Point1 = default,
        Vector2 Point2 = default,
        Vector2 Point3 = default,
        bool Polygon = false);

    private PreparedFixtureMove[]? _preparedFixtureMoves;
    private int _preparedFixtureMoveIndex = -1;
    private int _appliedFixtureMoves;

    // Runs while the solver owns a read-only view of fixtures and transforms. Only the
    // common grid-local leaf case uses this path, matching AddOrUpdateEntityTreeDown.
    internal PreparedFixtureMove PrepareFixtureMove(Entity<PhysicsComponent, TransformComponent> entity,
        Vector2 positionDelta, float angleDelta)
    {
        var (uid, body, xform) = entity;
        if (!body.CanCollide || xform.Anchored || xform.ChildCount != 0 ||
            xform.Broadphase is not { Valid: true, CanCollide: true } broadphase ||
            broadphase.Uid != xform.ParentUid || broadphase.Uid != xform.GridUid ||
            !_fixturesQuery.TryComp(uid, out var fixtures) || fixtures.Fixtures.Count != 1)
            return default;

        var position = xform.LocalPosition + positionDelta;
        var rotation = xform.LocalRotation + angleDelta;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !double.IsFinite(rotation.Theta) ||
            (xform.LocalPosition.EqualsApprox(position) && xform.LocalRotation.EqualsApprox(rotation)))
            return default;

        rotation = xform.NoLocalRotation ? xform.LocalRotation : SharedTransformSystem.NormalizeRotation(rotation);
        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (fixture.ProxyCount != 1)
                return default;

            var shape = fixture.Shape;
            switch (shape)
            {
                case PolygonShape { Vertices.Length: 4 } polygon:
                    var vertices = polygon.Vertices;
                    return new(uid, broadphase.Uid, position, rotation, shape.ComputeAABB(new Transform(position, rotation), 0),
                        shape.Radius, vertices[0], vertices[1], vertices[2], vertices[3], Polygon: true);

                case PhysShapeCircle circle:
                    return new(uid, broadphase.Uid, position, rotation, shape.ComputeAABB(new Transform(position, rotation), 0),
                        shape.Radius, circle.Position);

                default:
                    return default;
            }
        }

        return default;
    }

    internal void BeginPreparedFixtureMoves(PreparedFixtureMove[] moves)
    {
        _preparedFixtureMoves = moves;
        _appliedFixtureMoves = 0;
    }

    internal int EndPreparedFixtureMoves()
    {
        _preparedFixtureMoves = null;
        return _appliedFixtureMoves;
    }

    internal void ApplyPreparedFixtureMove(int index, EntityUid uid, Vector2 pos, Angle rot, TransformComponent xform)
    {
        _preparedFixtureMoveIndex = index;
        try
        {
            _transform.SetLocalPositionRotation(uid, pos, rot, xform);
        }
        finally
        {
            _preparedFixtureMoveIndex = -1;
        }
    }

    private bool TryApplyPreparedFixtureBounds(EntityUid uid, TransformComponent xform,
        FixturesComponent fixtures, IBroadPhase tree)
    {
        ref readonly var move = ref _preparedFixtureMoves![_preparedFixtureMoveIndex];
        // Earlier ECS events can move this entity, change its grid, or mutate its shape.
        // Validate the actual inputs to ComputeAABB, including in-place vertex changes.
        if (move.Owner != uid || move.Grid != xform.ParentUid ||
            !move.Position.Equals(xform.LocalPosition) || move.Rotation != xform.LocalRotation ||
            fixtures.Fixtures.Count != 1)
            return false;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (fixture.ProxyCount != 1 || !ReferenceEquals(fixture.ProxyTree, tree) || fixture.Shape.Radius != move.Radius)
                return false;

            var sameGeometry = fixture.Shape switch
            {
                PhysShapeCircle circle => !move.Polygon && circle.Position.Equals(move.Point0),
                PolygonShape { Vertices: [var a, var b, var c, var d] } => move.Polygon &&
                    a.Equals(move.Point0) && b.Equals(move.Point1) && c.Equals(move.Point2) && d.Equals(move.Point3),
                _ => false
            };

            if (!sameGeometry)
                return false;

            MoveFixtureProxy(tree, fixture.Proxies[0], move.Bounds);
            _appliedFixtureMoves++;
            return true;
        }

        return false;
    }
}
