using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using NUnit.Framework;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Profiling;
using Robust.Shared.Reflection;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

[TestFixture, NonParallelizable]
public sealed class PreparedBodyMoveTests
{
    [TestCase(1)]
    [TestCase(6)]
    [TestCase(12)]
    public void PreparedMovesPreserveCollisionsAndReentrantMoveOrder(int workers)
    {
        var sim = RobustServerSimulation.NewSimulation()
            .RegisterEntitySystems(systems => systems.LoadExtraSystemType<CollisionRecorder>())
            .InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var config = sim.Resolve<IConfigurationManager>();
        config.SetCVar(CVars.NetTickrate, 30);
        config.SetCVar(CVars.TargetMinimumTickrate, 30);
        config.SetCVar(CVars.ProfEnabled, true);
        config.SetCVar(CVars.PrepareFixtureMoves, true);
        var profiler = sim.Resolve<ProfManager>();
        profiler.Initialize();
        var maps = entities.System<SharedMapSystem>();
        var fixtures = entities.System<FixtureSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var collision = entities.System<CollisionRecorder>();
        var map = sim.CreateMap();

        var grid = maps.CreateGridEntity(map.MapId);
        var otherGrid = maps.CreateGridEntity(map.MapId);
        var tiles = new List<(Vector2i, Tile)>();
        for (var x = 0; x < 32; x++)
        for (var y = 0; y < 16; y++)
            tiles.Add((new Vector2i(x, y), new Tile(1)));

        maps.SetTiles(grid, tiles);
        maps.SetTiles(otherGrid, tiles);
        transform.SetLocalPosition(otherGrid, new Vector2(100, 0));

        (EntityUid Uid, PhysicsComponent Body, Fixture Fixture) Spawn(Vector2 position, Vector2 velocity,
            IPhysShape shape, float restitution = 0)
        {
            var uid = entities.SpawnEntity(null, new EntityCoordinates(grid, position));
            var body = entities.AddComponent<PhysicsComponent>(uid);
            var fixture = new Fixture(shape, 1, 1, true, restitution: restitution);
            fixtures.CreateFixture(uid, "body", fixture, body: body);
            physics.SetBodyType(uid, BodyType.Dynamic, body: body);
            physics.SetLinearDamping(uid, body, 0);
            physics.SetLinearVelocity(uid, velocity, body: body);
            return (uid, body, fixture);
        }

        Vector2[] Quad(float radius) => new[]
        {
            new Vector2(-radius, -radius), new Vector2(radius, -radius),
            new Vector2(radius, radius), new Vector2(-radius, radius)
        };

        var trigger = Spawn(new Vector2(1.5f, 1.5f), Vector2.UnitX, new PhysShapeCircle(0.1f));
        var polygon = new PolygonShape();
        polygon.Set(Quad(0.1f), 4);
        var resized = Spawn(new Vector2(3.5f, 1.5f), Vector2.UnitX, polygon);
        var reparented = Spawn(new Vector2(5.5f, 1.5f), Vector2.UnitX, new PhysShapeCircle(0.1f));
        var left = Spawn(new Vector2(10.5f, 1.5f), 4 * Vector2.UnitX, new PhysShapeCircle(0.25f), 1);
        var right = Spawn(new Vector2(11.5f, 1.5f), -4 * Vector2.UnitX, new PhysShapeCircle(0.25f), 1);
        collision.Left = left.Uid;
        collision.Right = right.Uid;
        var moved = new List<(EntityUid Entity, bool ParentChanged)>();
        var simulationThread = Environment.CurrentManagedThreadId;
        var movedOffThread = false;

        // Keep a large free-body batch and plenty of independent contact islands.
        for (var x = 0; x < 27; x++)
        for (var y = 3; y < 15; y++)
        {
            Spawn(new Vector2(x + 0.25f, y + 0.25f), Vector2.UnitX, new PhysShapeCircle(0.12f));
            if (x % 3 == 0)
                Spawn(new Vector2(x + 0.45f, y + 0.25f), Vector2.Zero, new PhysShapeCircle(0.12f));
        }

        var triggered = false;
        void OnMove(ref MoveEvent ev)
        {
            if (ev.Sender != trigger.Uid && ev.Sender != resized.Uid && ev.Sender != reparented.Uid)
                return;

            movedOffThread |= Environment.CurrentManagedThreadId != simulationThread;
            moved.Add((ev.Sender, ev.ParentChanged));
            if (ev.Sender != trigger.Uid || triggered)
                return;

            triggered = true;
            physics.SetVertices(resized.Uid, "body", resized.Fixture, polygon, Quad(0.2f));
            transform.SetCoordinates(reparented.Uid, new EntityCoordinates(otherGrid, new Vector2(6.5f, 1.5f)));
        }

        transform.OnGlobalMoveEvent += OnMove;
        var firstLog = profiler.Buffer.LogWriteOffset;
        ThreadPool.GetMinThreads(out var oldMin, out var minIo);
        ThreadPool.GetMaxThreads(out var oldMax, out var maxIo);
        try
        {
            Assert.That(Thread.CurrentThread.IsThreadPoolThread, Is.False);
            Assert.That(ThreadPool.SetMinThreads(workers, minIo), Is.True);
            Assert.That(ThreadPool.SetMaxThreads(workers, maxIo), Is.True);
            ThreadPool.GetMaxThreads(out var activeWorkers, out _);
            Assert.That(activeWorkers, Is.EqualTo(workers));

            physics.Update(1f / 30);
            Assert.That(moved, Is.EqualTo(new[]
            {
                (trigger.Uid, false), (reparented.Uid, true), (resized.Uid, false), (reparented.Uid, false)
            }));
            AssertProxy(resized.Uid, resized.Fixture);
            AssertProxy(reparented.Uid, reparented.Fixture);

            for (var i = 0; i < 5; i++)
                physics.Update(1f / 30);
        }
        finally
        {
            ThreadPool.SetMaxThreads(oldMax, maxIo);
            ThreadPool.SetMinThreads(oldMin, minIo);
            transform.OnGlobalMoveEvent -= OnMove;
        }

        void AssertProxy(EntityUid uid, Fixture fixture)
        {
            var xform = entities.GetComponent<TransformComponent>(uid);
            var expected = fixture.Shape.ComputeAABB(new Transform(xform.LocalPosition, xform.LocalRotation), 0);
            Assert.That(fixture.Proxies[0].AABB, Is.EqualTo(expected));
        }

        var applied = 0;
        var buffer = profiler.Buffer;
        for (var i = Math.Max(firstLog, buffer.LogWriteOffset - buffer.LogBuffer.Length); i < buffer.LogWriteOffset; i++)
        {
            var entry = buffer.Log(i);
            if (entry.Type == ProfLogType.Value && entry.Value.Value.Type == ProfValueType.Int32 &&
                profiler.GetString(entry.Value.StringId) == "Prepared fixture moves")
                applied += entry.Value.Value.Int32;
        }

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.GreaterThan(100), "The fixture preparation path must be exercised.");
            Assert.That(movedOffThread, Is.False);
            Assert.That(collision.Calls, Has.Count.EqualTo(2));
            Assert.That(collision.Calls, Is.EquivalentTo(new[] { left.Uid, right.Uid }));
            Assert.That(left.Body.LinearVelocity.X, Is.LessThan(0));
            Assert.That(right.Body.LinearVelocity.X, Is.GreaterThan(0));
            Assert.That(entities.GetComponent<TransformComponent>(reparented.Uid).ParentUid, Is.EqualTo(otherGrid.Owner));
        });
    }

    [Reflect(false)]
    private sealed class CollisionRecorder : EntitySystem
    {
        public EntityUid Left;
        public EntityUid Right;
        public readonly List<EntityUid> Calls = new();

        public override void Initialize()
        {
            SubscribeLocalEvent<PhysicsComponent, StartCollideEvent>(OnCollide);
        }

        private void OnCollide(Entity<PhysicsComponent> body, ref StartCollideEvent ev)
        {
            if (body.Owner == Left && ev.OtherEntity == Right || body.Owner == Right && ev.OtherEntity == Left)
                Calls.Add(body.Owner);
        }
    }
}
