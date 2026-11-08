using System;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

[TestFixture]
public sealed class GridMotionSubstepTests
{
    [Test]
    public void AdaptiveSubstepsShortenAfterFastMotionStops()
    {
        var sim = RobustServerSimulation.NewSimulation()
            .RegisterEntitySystems(systems => systems.LoadExtraSystemType<StopGridsAfterFirstStep>())
            .InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 60);
        cfg.SetCVar(CVars.LocalGridSubsteps, false);
        var maps = entities.System<SharedMapSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var probe = entities.System<StopGridsAfterFirstStep>();
        var map = sim.CreateMap();
        for (var i = 0; i < 2; i++)
        {
            var grid = maps.CreateGridEntity(map.MapId);
            maps.SetTile(grid, Vector2i.Zero, new Tile(1));
            transforms.SetLocalPosition(grid, new Vector2(i * 4, 0));
            physics.SetBodyType(grid, BodyType.Dynamic);
            physics.SetLinearVelocity(grid, new Vector2(i == 0 ? 200 : -200, 0));
            probe.Grids.Add(grid);
        }

        const float delta = 1f / 30;
        physics.Update(delta);

        Assert.Multiple(() =>
        {
            Assert.That(probe.FirstStep, Is.LessThan(delta / 2), "Approaching grids require fine initial steps.");
            Assert.That(probe.Steps, Is.LessThan(10), "The solver should return to coarse steps once the grids stop.");
            Assert.That(probe.Elapsed, Is.EqualTo(delta).Within(1e-6), "Repartitioning must simulate the full tick.");
        });
    }

    private sealed class StopGridsAfterFirstStep : EntitySystem
    {
        [Dependency] private SharedPhysicsSystem _physics = default!;

        public readonly List<EntityUid> Grids = new();
        public int Steps;
        public float FirstStep;
        public float Elapsed;

        public override void Initialize()
        {
            SubscribeLocalEvent<PhysicsUpdateAfterSolveEvent>(OnAfterSolve);
        }

        private void OnAfterSolve(ref PhysicsUpdateAfterSolveEvent ev)
        {
            Elapsed += ev.DeltaTime;
            if (++Steps != 1)
                return;

            FirstStep = ev.DeltaTime;
            foreach (var grid in Grids)
                _physics.SetLinearVelocity(grid, Vector2.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FastGridCollisionOnlySubstepsNearbyAndJointedBodies(bool attachDuringCollision)
    {
        var sim = RobustServerSimulation.NewSimulation()
            .RegisterEntitySystems(systems => systems.LoadExtraSystemType<AttachOnImpact>())
            .InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 60);
        var maps = entities.System<SharedMapSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var joints = entities.System<SharedJointSystem>();
        var map = sim.CreateMap();

        var grid = maps.CreateGridEntity(map.MapId);
        var wall = maps.CreateGridEntity(map.MapId);
        maps.SetTile(grid, Vector2i.Zero, new Tile(1));
        maps.SetTile(wall, Vector2i.Zero, new Tile(1));
        transforms.SetLocalPosition(wall, new Vector2(3, 0));
        physics.SetBodyType(grid, BodyType.Dynamic);
        physics.SetLinearVelocity(grid, new Vector2(240, 0));

        Entity<PhysicsComponent> BodyAt(float x, float speed)
        {
            var uid = entities.SpawnEntity(null, new MapCoordinates(x, 0, map.MapId));
            var body = entities.AddComponent<PhysicsComponent>(uid);
            entities.System<FixtureSystem>().CreateFixture(uid, "body",
                new Fixture(new PhysShapeCircle(0.1f), 1, 1, true), body: body);
            physics.SetBodyType(uid, BodyType.Dynamic, body: body);
            physics.SetLinearVelocity(uid, new Vector2(speed, 0), body: body);
            return (uid, body);
        }

        // The joint endpoint is outside the spatial search around the collision.
        var coupled = BodyAt(-80, 240);
        var impact = entities.System<AttachOnImpact>();
        if (attachDuringCollision)
        {
            impact.Grid = grid;
            impact.Peer = coupled;
        }
        else
        {
            joints.CreateDistanceJoint(grid, coupled);
            joints.Update(1f / 30);
        }

        var distant = BodyAt(100, 12);
        physics.SetLinearDamping(distant, distant.Comp, 6);

        physics.Update(1f / 30);

        Assert.Multiple(() =>
        {
            Assert.That(transforms.GetWorldPosition(grid).X, Is.LessThan(2.2f), "The fast hull should stop at the wall.");
            Assert.That(coupled.Comp.LinearVelocity.X, Is.LessThan(20), "The wall impulse should reach the distant joint endpoint.");
            Assert.That(impact.CreatedJoint, Is.EqualTo(attachDuringCollision));
            Assert.That(distant.Comp.LinearVelocity.X, Is.EqualTo(12 * 0.9f * 0.9f).Within(1e-5),
                "A late join must leave independent bodies at their ordinary cadence.");
            Assert.That(transforms.GetWorldPosition(distant).X, Is.EqualTo(100.342f).Within(1e-3),
                "An independent body must advance for the full tick.");
        });
    }

    private sealed class AttachOnImpact : EntitySystem
    {
        [Dependency] private SharedJointSystem _joints = default!;

        public EntityUid Grid;
        public EntityUid Peer;
        public bool CreatedJoint;

        public override void Initialize()
        {
            SubscribeLocalEvent<PhysicsComponent, StartCollideEvent>(OnStartCollide);
        }

        private void OnStartCollide(Entity<PhysicsComponent> entity, ref StartCollideEvent ev)
        {
            if (entity.Owner != Grid || CreatedJoint)
                return;

            CreatedJoint = true;
            _joints.CreateDistanceJoint(Grid, Peer);
            _joints.Update(0);
        }
    }

    [Test]
    public void FastGridContactCanJoinAfterTheFirstSubstep()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 60);
        var maps = entities.System<SharedMapSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var map = sim.CreateMap();

        EntityUid GridAt(float x)
        {
            var grid = maps.CreateGridEntity(map.MapId);
            maps.SetTile(grid, Vector2i.Zero, new Tile(1));
            transforms.SetLocalPosition(grid, new Vector2(x, 0));
            return grid;
        }

        var fast = GridAt(0);
        GridAt(5); // This wall requests the initial fine steps.
        var late = GridAt(80);
        physics.SetBodyType(fast, BodyType.Dynamic);
        physics.SetBodyType(late, BodyType.Dynamic);
        physics.SetLinearVelocity(fast, new Vector2(240, 0));

        var distant = entities.SpawnEntity(null, new MapCoordinates(200, 0, map.MapId));
        var distantBody = entities.AddComponent<PhysicsComponent>(distant);
        entities.System<FixtureSystem>().CreateFixture(distant, "body",
            new Fixture(new PhysShapeCircle(0.1f), 1, 1, true), body: distantBody);
        physics.SetBodyType(distant, BodyType.Dynamic, body: distantBody);
        physics.SetLinearDamping(distant, distantBody, 6);
        physics.SetLinearVelocity(distant, new Vector2(12, 0), body: distantBody);

        var inserted = false;
        void OnMove(ref MoveEvent ev)
        {
            if (ev.Sender != fast || inserted)
                return;

            inserted = true;
            transforms.SetLocalPosition(late, new Vector2(2.5f, 0));
        }

        transforms.OnGlobalMoveEvent += OnMove;
        try
        {
            physics.Update(1f / 30);
        }
        finally
        {
            transforms.OnGlobalMoveEvent -= OnMove;
        }

        Assert.Multiple(() =>
        {
            Assert.That(inserted, Is.True);
            Assert.That(transforms.GetWorldPosition(late).X, Is.GreaterThan(2.6f),
                "The late grid should receive an impulse from the fast grid.");
            Assert.That(transforms.GetWorldPosition(fast).X, Is.LessThan(transforms.GetWorldPosition(late).X),
                "The fast grid must stay on the near side of the new contact.");
            Assert.That(distantBody.LinearVelocity.X, Is.EqualTo(12 * 0.9f * 0.9f).Within(1e-5),
                "Late contacts must leave unrelated bodies at the ordinary cadence.");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DistantFastBodyDoesNotAmplifyNearbySlowCollision(bool differentMap)
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 60);
        var maps = entities.System<SharedMapSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var map = sim.CreateMap();
        var grid = maps.CreateGridEntity(map.MapId);
        maps.SetTile(grid, Vector2i.Zero, new Tile(1));
        var obstacle = maps.CreateGridEntity(map.MapId);
        maps.SetTile(obstacle, Vector2i.Zero, new Tile(1));
        transforms.SetLocalPosition(obstacle, new Vector2(1.2f, 0));
        var body = entities.GetComponent<PhysicsComponent>(grid);
        physics.SetBodyType(grid, BodyType.Dynamic, body: body);
        physics.SetLinearVelocity(grid, Vector2.UnitX, body: body);

        var fastMap = differentMap ? sim.CreateMap().MapId : map.MapId;
        var fast = entities.SpawnEntity(null, new MapCoordinates(new Vector2(1000, 0), fastMap));
        var fastBody = entities.AddComponent<PhysicsComponent>(fast);
        entities.System<FixtureSystem>().CreateFixture(fast, "body",
            new Fixture(new PhysShapeCircle(0.1f), 1, 1, true), body: fastBody);
        physics.SetBodyType(fast, BodyType.Dynamic, body: fastBody);
        physics.SetLinearVelocity(fast, new Vector2(300, 0), body: fastBody);

        var method = typeof(SharedPhysicsSystem).GetMethod("GetGridMotionSubsteps", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.That((int) method.Invoke(physics, new object[] {1f / 30, false})!, Is.EqualTo(2),
            "An unrelated fast body must not increase the work for a slow nearby grid collision.");
    }

    [TestCase(100f, false)]
    [TestCase(300f, false)]
    [TestCase(400f, false)]
    [TestCase(300f, true)]
    public void NearbyNonGridBodySubstepsFollowRelativeSweep(float speed, bool movingAway)
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 60);
        var maps = entities.System<SharedMapSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var map = sim.CreateMap();
        var grid = maps.CreateGridEntity(map.MapId);
        maps.SetTile(grid, Vector2i.Zero, new Tile(1));
        var body = entities.GetComponent<PhysicsComponent>(grid);
        physics.SetBodyType(grid, BodyType.Dynamic, body: body);
        physics.SetLinearVelocity(grid, -Vector2.UnitX, body: body);
        var fast = entities.SpawnEntity(null, new MapCoordinates(new Vector2(-2, 0.5f), map.MapId));
        var fastBody = entities.AddComponent<PhysicsComponent>(fast);
        entities.System<FixtureSystem>().CreateFixture(fast, "body",
            new Fixture(new PhysShapeCircle(0.1f), 1, 1, true), body: fastBody);
        physics.SetBodyType(fast, BodyType.Dynamic, body: fastBody);
        physics.SetLinearVelocity(fast, new Vector2(movingAway ? -speed : speed, 0), body: fastBody);

        var method = typeof(SharedPhysicsSystem).GetMethod("GetGridMotionSubsteps", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var steps = (int) method.Invoke(physics, new object[] {1f / 30, false})!;
        if (movingAway)
            Assert.That(steps, Is.EqualTo(2), "A receding body cannot hit the hull during this tick.");
        else
            Assert.That(steps, Is.GreaterThanOrEqualTo((int) MathF.Ceiling((speed + 1) / 30 / 0.5f)));
    }

    [TestCase(-1f, false)]
    [TestCase(-4f, true)]
    public void DiagonalSweepOnlySubstepsForSimultaneousOverlap(float startY, bool crossesGrid)
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 60);
        var maps = entities.System<SharedMapSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var map = sim.CreateMap();
        var grid = maps.CreateGridEntity(map.MapId);
        maps.SetTile(grid, Vector2i.Zero, new Tile(1));
        physics.SetBodyType(grid, BodyType.Dynamic);
        physics.SetLinearVelocity(grid, -Vector2.UnitX);

        var debris = entities.SpawnEntity(null, new MapCoordinates(new Vector2(-4, startY), map.MapId));
        var body = entities.AddComponent<PhysicsComponent>(debris);
        entities.System<FixtureSystem>().CreateFixture(debris, "body",
            new Fixture(new PhysShapeCircle(0.1f), 1, 1, true), body: body);
        physics.SetBodyType(debris, BodyType.Dynamic, body: body);
        physics.SetLinearVelocity(debris, new Vector2(200, 200), body: body);

        var method = typeof(SharedPhysicsSystem).GetMethod("GetGridMotionSubsteps", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var steps = (int) method.Invoke(physics, new object[] { 1f / 30, false })!;
        Assert.That(steps, crossesGrid ? Is.GreaterThan(2) : Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NearbyFastGridsUseRelativeMotion(bool approaching)
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 60);
        var maps = entities.System<SharedMapSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var map = sim.CreateMap();
        for (var i = 0; i < 2; i++)
        {
            var grid = maps.CreateGridEntity(map.MapId);
            maps.SetTile(grid, Vector2i.Zero, new Tile(1));
            transforms.SetLocalPosition(grid, new Vector2(i * 4, 0));
            var body = entities.GetComponent<PhysicsComponent>(grid);
            physics.SetBodyType(grid, BodyType.Dynamic, body: body);
            physics.SetLinearVelocity(grid, new Vector2(i == 1 && approaching ? -200 : 200, 0), body: body);
        }
        var method = typeof(SharedPhysicsSystem).GetMethod("GetGridMotionSubsteps", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var steps = (int) method.Invoke(physics, new object[] {1f / 30, false})!;
        Assert.That(steps, Is.EqualTo(approaching ? 27 : 2));
    }

    [TestCase(100f)]
    [TestCase(200f)]
    [TestCase(400f)]
    public void FastDebrisContactsOutsideOfHull(float speed)
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 60);
        var maps = entities.System<SharedMapSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var map = sim.CreateMap();
        var hull = maps.CreateGridEntity(map.MapId);
        for (var x = 0; x < 12; x++)
        for (var y = 0; y < 6; y++)
            maps.SetTile(hull, new Vector2i(x, y), new Tile(1));
        var debris = maps.CreateGridEntity(map.MapId);
        maps.SetTile(debris, Vector2i.Zero, new Tile(1));
        transforms.SetLocalPosition(debris, new Vector2(-2, 2));
        var body = entities.GetComponent<PhysicsComponent>(debris);
        physics.SetBodyType(debris, BodyType.Dynamic, body: body);
        physics.SetSleepingAllowed(debris, body, false);
        physics.SetLinearVelocity(debris, new Vector2(speed, 0), body: body);
        physics.Update(1f / 30);
        physics.Update(1f / 30);
        Assert.That(transforms.GetWorldPosition(debris).X, Is.LessThan(0.05f), "Debris must be stopped at the outside, not deep inside the hull");
    }

    [Test]
    public void EmptySpaceKeepsConfiguredSubsteps()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 60);
        var maps = entities.System<SharedMapSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var map = sim.CreateMap();
        var grid = maps.CreateGridEntity(map.MapId);
        maps.SetTile(grid, Vector2i.Zero, new Tile(1));
        var body = entities.GetComponent<PhysicsComponent>(grid);
        physics.SetBodyType(grid, BodyType.Dynamic, body: body);
        physics.SetLinearVelocity(grid, new Vector2(400, 0), body: body);
        var method = typeof(SharedPhysicsSystem).GetMethod("GetGridMotionSubsteps", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.That((int) method.Invoke(physics, new object[] {1f / 30, false})!, Is.EqualTo(2));
    }
    [Test]
    public void RotationOfLongGridAlsoIncreasesSubsteps()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var map = sim.CreateMap();
        var grid = maps.CreateGridEntity(map.MapId);
        for (var x = 0; x < 40; x++)
            maps.SetTile(grid, new Vector2i(x, 0), new Tile(1));
        var obstacle = maps.CreateGridEntity(map.MapId);
        maps.SetTile(obstacle, new Vector2i(39, 2), new Tile(1));
        var body = entities.GetComponent<PhysicsComponent>(grid);
        physics.SetBodyType(grid, BodyType.Dynamic, body: body);
        physics.SetFixedRotation(grid, false, body: body);
        physics.SetAngularVelocity(grid, 2f, body: body);
        var method = typeof(SharedPhysicsSystem).GetMethod("GetGridMotionSubsteps", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.That((int) method.Invoke(physics, new object[] {1f / 30, false})!, Is.GreaterThan(2));
    }
}
