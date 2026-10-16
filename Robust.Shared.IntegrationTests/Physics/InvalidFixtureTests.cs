using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.EntitySerialization.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

// Deliberately corrupt geometry to exercise the server's containment of invalid runtime data.
#pragma warning disable RA0002

[TestFixture]
public sealed class InvalidFixtureTests
{
    [Test]
    public void EmptyPolygonIsRemovedBeforePhysicsInitializationAndLookup()
    {
        var sim = CreateSimulation();
        var em = sim.Resolve<IEntityManager>();
        var (_, map) = sim.CreateMap();
        var uid = em.CreateEntityUninitialized(null, new MapCoordinates(Vector2.Zero, map));
        var fixtures = em.AddComponent<FixturesComponent>(uid);
        fixtures.Fixtures.Add("broken", new Fixture(new PolygonShape(), 1, 1, true));
        em.AddComponent<PhysicsComponent>(uid);

        Assert.DoesNotThrow(() => em.InitializeAndStartEntity(uid));
        Assert.That(fixtures.FixtureCount, Is.Zero);
        Assert.That(em.IsQueuedForDeletion(uid), Is.True);
        var events = em.System<InvalidFixtureTestSystem>().Rejected;
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].Entity, Is.EqualTo(uid));
        Assert.That(events[0].FixtureId, Is.EqualTo("broken"));
        Assert.That(events[0].Reason, Does.Contain("0 vertices"));
        Assert.DoesNotThrow(() => em.System<EntityLookupSystem>().GetAABBNoContainer(uid, Vector2.Zero, default));
        Assert.DoesNotThrow(() => em.DeleteEntity(uid));
    }

    [Test]
    public void FixtureWithoutPhysicsIsStillQuarantinedBeforeEntityLookup()
    {
        var sim = CreateSimulation();
        var em = sim.Resolve<IEntityManager>();
        var (_, map) = sim.CreateMap();
        var uid = em.CreateEntityUninitialized(null, new MapCoordinates(Vector2.Zero, map));
        var fixtures = em.AddComponent<FixturesComponent>(uid);
        fixtures.Fixtures.Add("broken", new Fixture(new PolygonShape(), 0, 0, false));
        Assert.DoesNotThrow(() => em.InitializeAndStartEntity(uid));
        Assert.That(fixtures.FixtureCount, Is.Zero);
        Assert.That(em.IsQueuedForDeletion(uid), Is.True);
        Assert.DoesNotThrow(() => em.DeleteEntity(uid));
    }

    [Test]
    public void InvalidRuntimeFixtureIsNeverInsertedIntoTheBroadphase()
    {
        var sim = CreateSimulation();
        var em = sim.Resolve<IEntityManager>();
        var (_, map) = sim.CreateMap();
        var uid = em.Spawn(null, new MapCoordinates(Vector2.Zero, map));
        em.AddComponent<PhysicsComponent>(uid);
        var system = em.System<FixtureSystem>();
        var valid = new PolygonShape();
        valid.SetAsBox(0.5f, 0.5f);
        Assert.That(system.TryCreateFixture(uid, valid, "valid", collisionLayer: 1), Is.True);

        Assert.That(system.TryCreateFixture(uid, new PolygonShape(), "broken", collisionLayer: 1), Is.False);
        Assert.That(em.GetComponent<FixturesComponent>(uid).Fixtures.ContainsKey("broken"), Is.False);
        Assert.That(em.IsQueuedForDeletion(uid), Is.True);
        Assert.That(em.System<InvalidFixtureTestSystem>().Rejected, Has.Count.EqualTo(1));
        Assert.DoesNotThrow(() => em.DeleteEntity(uid));
    }

    [Test]
    public void FixtureUpdateRemovesGeometryThatWasInvalidatedAfterCreation()
    {
        var sim = CreateSimulation();
        var em = sim.Resolve<IEntityManager>();
        var (_, map) = sim.CreateMap();
        var uid = em.Spawn(null, new MapCoordinates(Vector2.Zero, map));
        em.AddComponent<PhysicsComponent>(uid);
        var system = em.System<FixtureSystem>();
        var polygon = new PolygonShape();
        polygon.SetAsBox(0.5f, 0.5f);
        system.TryCreateFixture(uid, polygon, "changed");
        polygon.Vertices = [];

        Assert.DoesNotThrow(() => system.FixtureUpdate(uid));
        Assert.That(em.GetComponent<FixturesComponent>(uid).FixtureCount, Is.Zero);
        Assert.That(em.IsQueuedForDeletion(uid), Is.True);
        Assert.DoesNotThrow(() => em.DeleteEntity(uid));
    }

    [Test]
    public void ValidThinFixtureAndCircleRemainUsable()
    {
        var sim = CreateSimulation();
        var em = sim.Resolve<IEntityManager>();
        var (_, map) = sim.CreateMap();
        var uid = em.Spawn(null, new MapCoordinates(Vector2.Zero, map));
        em.AddComponent<PhysicsComponent>(uid);
        var system = em.System<FixtureSystem>();
        var thin = new PolygonShape();
        thin.SetAsBox(0.5f, 0.001f);

        Assert.That(system.TryCreateFixture(uid, thin, "thin"), Is.True);
        Assert.That(system.TryCreateFixture(uid, new PhysShapeCircle(0.5f), "circle"), Is.True);
        Assert.That(em.IsQueuedForDeletion(uid), Is.False);
        Assert.That(em.System<InvalidFixtureTestSystem>().Rejected, Is.Empty);
    }

    [Test]
    public void NonFiniteGeometryAndMaterialAreRejected()
    {
        var circle = new PhysShapeCircle(float.NaN);
        Assert.That(FixtureSystem.ValidateFixture(new Fixture(circle, 0, 0, true), out _), Is.False);
        var polygon = new PolygonShape();
        polygon.SetAsBox(0.5f, 0.5f);
        polygon.Vertices[0] = new Vector2(float.PositiveInfinity, 0);
        Assert.That(FixtureSystem.ValidateFixture(new Fixture(polygon, 0, 0, true), out _), Is.False);
        Assert.That(FixtureSystem.ValidateFixture(new Fixture(new PhysShapeCircle(), 0, 0, true,
            density: float.NaN), out _), Is.False);
    }

    private static ISimulation CreateSimulation() => RobustServerSimulation.NewSimulation()
        .RegisterEntitySystems(s => s.LoadExtraSystemType<InvalidFixtureTestSystem>()).InitializeInstance();

    public sealed class InvalidFixtureTestSystem : EntitySystem
    {
        public readonly List<InvalidPhysicsFixtureEvent> Rejected = new();

        public override void Initialize()
        {
            SubscribeLocalEvent<InvalidPhysicsFixtureEvent>((ref InvalidPhysicsFixtureEvent ev) => Rejected.Add(ev));
            // Reproduce content pathfinding looking up the AABB during physics initialization.
            SubscribeLocalEvent<CollisionLayerChangeEvent>((ref CollisionLayerChangeEvent ev) =>
                EntityManager.System<EntityLookupSystem>().GetAABBNoContainer(ev.Body, Vector2.Zero, default));
        }
    }
}

[TestFixture]
public sealed class InvalidFixtureMapTests : RobustIntegrationTest
{
    [Test]
    public async Task MapLoadsAndDeletesOnlyTheObjectWithCollapsedBounds()
    {
        var server = StartServer();
        await server.WaitIdleAsync();
        var em = server.ResolveDependency<IEntityManager>();
        EntityUid broken = default;
        EntityUid healthy = default;
        EntityUid loadedMap = default;
        await server.WaitAssertion(() =>
        {
            using var reader = new StringReader("""
                meta:
                  format: 7
                  category: Map
                maps: [1]
                grids: [2]
                orphans: []
                nullspace: []
                tilemap: {}
                entities:
                - proto: ""
                  entities:
                  - uid: 1
                    components:
                    - type: Map
                  - uid: 2
                    components:
                    - type: MapGrid
                    - type: Transform
                      parent: 1
                  - uid: 3
                    components:
                    - type: MetaData
                      name: BrokenFixtureTest
                    - type: Transform
                      parent: 2
                    - type: Physics
                    - type: Fixtures
                      fixtures:
                        broken:
                          shape: !type:PhysShapeAabb
                            bounds: "-0.2,0.5,0.2,0.5"
                  - uid: 4
                    components:
                    - type: MetaData
                      name: HealthyFixtureTest
                    - type: Transform
                      parent: 2
                    - type: Physics
                    - type: Fixtures
                      fixtures:
                        healthy:
                          shape: !type:PhysShapeAabb
                            bounds: "-0.2,0.35,0.2,0.5"
                """);
            Assert.That(em.System<MapLoaderSystem>().TryLoadMap(reader, "invalid-fixture-regression", out var map, out _), Is.True);
            loadedMap = map!.Value.Owner;
            var query = em.AllEntityQueryEnumerator<MetaDataComponent>();
            while (query.MoveNext(out var uid, out var meta))
            {
                if (meta.EntityName == "BrokenFixtureTest") broken = uid;
                if (meta.EntityName == "HealthyFixtureTest") healthy = uid;
            }
            Assert.That(broken, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(healthy, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(em.IsQueuedForDeletion(broken), Is.True);
            Assert.That(em.IsQueuedForDeletion(healthy), Is.False);
        });
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(em.EntityExists(broken), Is.False);
            Assert.That(em.EntityExists(healthy), Is.True);
            Assert.That(em.EntityExists(loadedMap), Is.True);
        });
    }
}
