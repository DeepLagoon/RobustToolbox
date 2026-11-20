using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

[TestFixture]
public sealed class TypedLookupMapTests
{
    [Test]
    public void TypedRangeQueryPreservesRotatedNestedFixturesAcrossMapTransfers()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var transform = entities.System<SharedTransformSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var fixtures = entities.System<FixtureSystem>();
        var lookup = entities.System<EntityLookupSystem>();
        var first = sim.CreateMap();
        var second = sim.CreateMap();
        EntityUid Root(EntityUid map)
        {
            var root = entities.SpawnEntity(null, new EntityCoordinates(map, new Vector2(1000, -1000)));
            transform.SetLocalRotation(root, new Angle(.75));
            return root;
        }
        var firstRoot = Root(first.Uid);
        var secondRoot = Root(second.Uid);
        EntityUid Parent(EntityUid root)
        {
            var parent = entities.SpawnEntity(null, new EntityCoordinates(root, new Vector2(40, 20)));
            transform.SetLocalRotation(parent, new Angle(.38));
            return parent;
        }
        var firstParent = Parent(firstRoot);
        var secondParent = Parent(secondRoot);
        EntityUid Body(EntityUid parent, Vector2 position, bool hard)
        {
            var uid = entities.SpawnEntity(null, new EntityCoordinates(parent, position));
            var body = entities.AddComponent<PhysicsComponent>(uid);
            fixtures.CreateFixture(uid, "body", new Fixture(new PhysShapeCircle(.1f), 1, 1, hard), body: body);
            physics.SetBodyType(uid, BodyType.Dynamic, body: body);
            physics.SetCanCollide(uid, true, body: body);
            return uid;
        }
        var local = Body(firstParent, new Vector2(12, 8), true);
        var sensor = Body(firstParent, new Vector2(12.2f, 8), false);
        var foreign = Body(secondParent, new Vector2(12, 8), true);
        var outside = Body(firstParent, new Vector2(120, 80), true);
        var center = transform.GetMapCoordinates(local);
        var result = new HashSet<Entity<PhysicsComponent>>();
        const LookupFlags flags = LookupFlags.Approximate | LookupFlags.Dynamic | LookupFlags.Sensors;
        void AssertNested(EntityUid parent, EntityUid root, EntityUid child, EntityUid mapUid, MapId map)
        {
            Assert.That(entities.GetComponent<TransformComponent>(root).ParentUid, Is.EqualTo(mapUid));
            var parentTransform = entities.GetComponent<TransformComponent>(parent);
            Assert.That(parentTransform.ParentUid, Is.EqualTo(root));
            Assert.That(parentTransform.MapUid, Is.EqualTo(mapUid));
            Assert.That(parentTransform.MapID, Is.EqualTo(map));
            Assert.That(parentTransform.GridUid, Is.Null);
            var childTransform = entities.GetComponent<TransformComponent>(child);
            Assert.That(childTransform.ParentUid, Is.EqualTo(parent));
            Assert.That(childTransform.GridUid, Is.Null);
            Assert.That(childTransform.MapUid, Is.EqualTo(mapUid));
            Assert.That(childTransform.MapID, Is.EqualTo(map));
        }
        void Query(params EntityUid[] expected)
        {
            result.Clear();
            // Four physics components and a 256 square metre area select the typed
            // enumeration path, including its foreign-map rejection predicate.
            lookup.GetEntitiesInRange(center, 8f, result, flags);
            Assert.That(result.Select(e => e.Owner), Is.EquivalentTo(expected));
            Assert.That(result.All(e => entities.GetComponent<TransformComponent>(e).MapID == first.MapId), Is.True);
            Assert.That(result.Select(e => e.Owner), Does.Not.Contain(outside));
        }
        Assert.That(transform.GetWorldPosition(local), Is.EqualTo(transform.GetWorldPosition(foreign)),
            "The foreign nested fixture overlaps geometrically, but belongs to another map.");
        AssertNested(firstParent, firstRoot, local, first.Uid, first.MapId);
        AssertNested(secondParent, secondRoot, foreign, second.Uid, second.MapId);
        Query(local, sensor);

        // Native reparenting must propagate map membership through a rotated
        // hierarchy even when the resulting geometric world pose is unchanged.
        transform.SetCoordinates(secondParent, new EntityCoordinates(firstRoot, new Vector2(40, 20)));
        AssertNested(secondParent, firstRoot, foreign, first.Uid, first.MapId);
        Query(local, sensor, foreign);
        transform.SetCoordinates(secondParent, new EntityCoordinates(secondRoot, new Vector2(40, 20)));
        AssertNested(secondParent, secondRoot, foreign, second.Uid, second.MapId);
        Query(local, sensor);

        // Same-map live transform changes must still affect overlap immediately.
        transform.SetLocalPosition(firstParent, new Vector2(140, 120));
        AssertNested(firstParent, firstRoot, local, first.Uid, first.MapId);
        Query();
        transform.SetLocalPosition(firstParent, new Vector2(40, 20));
        AssertNested(firstParent, firstRoot, local, first.Uid, first.MapId);
        Query(local, sensor);
    }
}
