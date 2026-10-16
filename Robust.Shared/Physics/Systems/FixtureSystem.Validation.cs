using System;
using System.Numerics;
using Robust.Shared.Collections;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Network;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Shapes;

namespace Robust.Shared.Physics.Systems;

public sealed partial class FixtureSystem
{
    [Dependency] private readonly INetManager _net = default!;

    // Validate before mass calculation, broadphase insertion, or collision-change subscribers can
    // inspect shapes. In particular, Box2 now clamps inverted bounds to a degenerate rectangle;
    // Fixture.AfterDeserialization can consequently produce a PolygonShape with no vertices.
    internal void RemoveInvalidFixtures(EntityUid uid, FixturesComponent manager, PhysicsComponent? body = null,
        bool initializing = false)
    {
        if (!_net.IsServer)
            return;

        var invalid = new ValueList<(string Id, string Reason)>();
        foreach (var (id, fixture) in manager.Fixtures)
        {
            if (string.IsNullOrEmpty(id))
                invalid.Add((id, "empty fixture ID"));
            else if (!ValidateFixture(fixture, out var reason))
                invalid.Add((id, reason));
        }

        // Remove every unsafe shape before raising the event: listeners may inspect this entity.
        foreach (var (id, _) in invalid.Span)
        {
            if (initializing)
                manager.Fixtures.Remove(id);
            else
                DestroyFixture(uid, id, updates: false, body: body, manager: manager);
        }

        foreach (var (id, reason) in invalid.Span)
            RejectFixture(uid, id, reason);
    }

    private bool RejectInvalidFixture(EntityUid uid, string id, Fixture fixture)
    {
        if (!_net.IsServer)
            return false;

        string reason;
        if (string.IsNullOrEmpty(id))
            reason = "empty fixture ID";
        else if (ValidateFixture(fixture, out reason))
            return false;
        RejectFixture(uid, id, reason);
        return true;
    }

    private void RejectFixture(EntityUid uid, string id, string reason)
    {
        Log.Warning($"Rejected unsafe fixture '{id}' on {ToPrettyString(uid)}: {reason}. Queuing entity for deletion.");
        QueueDel(uid);
        var ev = new InvalidPhysicsFixtureEvent(uid, id, reason);
        RaiseLocalEvent(ref ev);
    }

    internal static bool ValidateFixture(Fixture fixture, out string reason)
    {
        reason = "non-finite or negative fixture material property";
        if (!NonNegative(fixture.Density) || !NonNegative(fixture.Friction) || !NonNegative(fixture.Restitution))
            return false;

        var shape = fixture.Shape;
        reason = "non-finite or negative shape radius";
        if (shape == null || !NonNegative(shape.Radius))
            return false;

        switch (shape)
        {
            case PolygonShape polygon:
                return ValidatePolygon(polygon, out reason);
            case Polygon polygon:
                reason = "polygon vertex count outside supported range";
                return polygon.VertexCount is >= 3 and <= PhysicsConstants.MaxPolygonVertices &&
                       ValidatePolygon(new PolygonShape(polygon), out reason);
            case SlimPolygon polygon:
                return ValidatePolygon(new PolygonShape(polygon), out reason);
            case PhysShapeAabb box:
                var bounds = box.LocalBounds;
                reason = "degenerate or non-finite rectangle bounds";
                return Finite(bounds.BottomLeft) && Finite(bounds.TopRight) &&
                       bounds.Width > 0 && bounds.Height > 0 &&
                       float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height);
            case PhysShapeCircle circle:
                reason = "non-finite circle position";
                return Finite(circle.Position);
            case EdgeShape edge:
                reason = "degenerate or non-finite edge";
                return Finite(edge.Vertex0) && Finite(edge.Vertex1) && Finite(edge.Vertex2) &&
                       Finite(edge.Vertex3) && edge.Vertex1 != edge.Vertex2;
            case ChainShape chain:
                reason = "empty, degenerate or non-finite chain";
                if (chain.ChildCount <= 0 || !Finite(chain.PrevVertex) || !Finite(chain.NextVertex))
                    return false;
                for (var i = 0; i < chain.Vertices.Length; i++)
                {
                    if (!Finite(chain.Vertices[i]) || i > 0 && chain.Vertices[i] == chain.Vertices[i - 1])
                        return false;
                }
                return true;
            default:
                reason = $"unsupported fixture shape {shape.GetType().Name}";
                return false;
        }
    }

    private static bool ValidatePolygon(PolygonShape polygon, out string reason)
    {
        reason = $"polygon has {polygon.VertexCount} vertices (expected 3-{PhysicsConstants.MaxPolygonVertices})";
        if (polygon.VertexCount is < 3 or > PhysicsConstants.MaxPolygonVertices)
            return false;

        reason = "polygon has missing normals or non-finite geometry";
        if (polygon.Normals.Length != polygon.VertexCount || !Finite(polygon.Centroid))
            return false;
        for (var i = 0; i < polygon.VertexCount; i++)
        {
            if (!Finite(polygon.Vertices[i]) || !Finite(polygon.Normals[i]) || polygon.Normals[i] == Vector2.Zero)
                return false;
        }

        // Do not use ValidateHull's welding tolerance: existing thin, non-degenerate fixtures
        // produced by SetAsBox are valid. Only reject non-convex/zero-area geometry here.
        reason = "polygon is degenerate or not a valid convex hull";
        for (var i = 0; i < polygon.VertexCount; i++)
        {
            var a = polygon.Vertices[i];
            var b = polygon.Vertices[(i + 1) % polygon.VertexCount];
            for (var j = 0; j < polygon.VertexCount; j++)
            {
                if (j == i || j == (i + 1) % polygon.VertexCount)
                    continue;
                var c = polygon.Vertices[j];
                var cross = ((double) b.X - a.X) * ((double) c.Y - a.Y) -
                            ((double) b.Y - a.Y) * ((double) c.X - a.X);
                if (cross <= 0)
                    return false;
            }
        }
        return true;
    }

    private static bool Finite(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
    private static bool NonNegative(float value) => float.IsFinite(value) && value >= 0;
}
