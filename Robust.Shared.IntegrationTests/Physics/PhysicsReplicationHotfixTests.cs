using System.Numerics;
using Moq;
using NUnit.Framework;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

[TestFixture]
public sealed class PhysicsReplicationHotfixTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void RestingBodyOnDistantRotatedGridDoesNotDriftOrDirty(bool offsetCenter)
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 30);
        var timing = sim.Resolve<IGameTiming>();
        Mock.Get(timing).SetupProperty(t => t.CurTick, GameTick.First);
        var maps = entities.System<SharedMapSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var grid = maps.CreateGridEntity(sim.CreateMap().MapId);
        maps.SetTile(grid, Vector2i.Zero, new Tile(1));
        transforms.SetLocalPositionRotation(grid, new Vector2(1994.3861f, -22616.959f), new Angle(2.413));
        var uid = entities.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(44.48584f, 26.044922f)));
        transforms.SetLocalRotation(uid, new Angle(0.317));
        var body = entities.AddComponent<PhysicsComponent>(uid);
        var center = offsetCenter ? new Vector2(0.25f, -0.125f) : Vector2.Zero;
        entities.System<FixtureSystem>().CreateFixture(uid, "body",
            new Fixture(new PhysShapeCircle(0.1f, center), 1, 1, true), body: body);
        physics.SetBodyType(uid, BodyType.Dynamic, body: body);
        physics.SetSleepingAllowed(uid, body, false);
        physics.Update(1f / 30);
        var xform = entities.GetComponent<TransformComponent>(uid);
        var originalPosition = xform.LocalPosition;
        var originalRotation = xform.LocalRotation;
        var fromTick = timing.CurTick + 1;
        timing.CurTick = fromTick + 1;
        var moves = 0;
        void OnMove(ref MoveEvent ev) { if (ev.Sender == uid) moves++; }
        transforms.OnGlobalMoveEvent += OnMove;
        try
        {
            for (var i = 0; i < 30; i++)
            {
                physics.Update(1f / 30);
                timing.CurTick += 1;
            }
        }
        finally { transforms.OnGlobalMoveEvent -= OnMove; }
        Assert.Multiple(() =>
        {
            Assert.That(xform.LocalPosition, Is.EqualTo(originalPosition));
            Assert.That(xform.LocalRotation, Is.EqualTo(originalRotation));
            Assert.That(moves, Is.Zero);
            Assert.That(EntityManager.GetModifiedAspects(xform, fromTick), Is.Zero);
        });
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void TransformReplicatesOnlyChangedPoseFields(bool translate, bool rotate)
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var timing = sim.Resolve<IGameTiming>();
        Mock.Get(timing).SetupProperty(t => t.CurTick, GameTick.First);
        var uid = entities.SpawnEntity(null, new MapCoordinates(Vector2.One, sim.CreateMap().MapId));
        var transforms = entities.System<SharedTransformSystem>();
        var xform = entities.GetComponent<TransformComponent>(uid);
        var position = translate ? 2 * Vector2.One : xform.LocalPosition;
        var rotation = rotate ? Angle.FromDegrees(90) : xform.LocalRotation;
        var fromTick = timing.CurTick + 1;
        timing.CurTick = fromTick + 1;
        var moves = 0;
        void OnMove(ref MoveEvent ev) { if (ev.Sender == uid) moves++; }
        transforms.OnGlobalMoveEvent += OnMove;
        try
        {
            transforms.SetLocalPositionRotation(uid, position, rotation, xform);
            transforms.SetLocalPositionRotation(uid, position, rotation, xform);
        }
        finally { transforms.OnGlobalMoveEvent -= OnMove; }

        var fields = entities.ComponentFactory.GetRegistration(CompIdx.Index<TransformComponent>()).NetworkedFieldLookup;
        var expected = (translate ? 1UL << fields[nameof(TransformComponent.LocalPosition)] : 0UL)
            | (rotate ? 1UL << fields[nameof(TransformComponent.LocalRotation)] : 0UL);
        Assert.Multiple(() =>
        {
            Assert.That(EntityManager.GetModifiedAspects(xform, fromTick), Is.EqualTo(expected));
            Assert.That(xform.LocalPosition, Is.EqualTo(position));
            Assert.That(xform.LocalRotation, Is.EqualTo(rotation));
            Assert.That(moves, Is.EqualTo(1), "Repeating the same pose must not raise another move event.");
        });
    }

    [Test]
    public void RotatingOffsetBodyKeepsItsCenterOfMass()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 30);
        var transforms = entities.System<SharedTransformSystem>();
        var physics = entities.System<SharedPhysicsSystem>();
        var uid = entities.SpawnEntity(null, new MapCoordinates(new Vector2(3, 5), sim.CreateMap().MapId));
        var body = entities.AddComponent<PhysicsComponent>(uid);
        entities.System<FixtureSystem>().CreateFixture(uid, "body",
            new Fixture(new PhysShapeCircle(0.1f, new Vector2(0.25f, -0.125f)), 1, 1, true), body: body);
        physics.SetBodyType(uid, BodyType.Dynamic, body: body);
        physics.SetFixedRotation(uid, false, body: body);
        physics.SetLinearDamping(uid, body, 0);
        physics.SetAngularDamping(uid, body, 0);
        physics.SetSleepingAllowed(uid, body, false);
        physics.SetAngularVelocity(uid, 2, body: body);
        var xform = entities.GetComponent<TransformComponent>(uid);
        Vector2 Center()
        {
            var (position, rotation) = transforms.GetWorldPositionRotation(xform);
            return Transform.Mul(new Transform(position, rotation), body.LocalCenter);
        }
        var center = Center();
        var position = xform.LocalPosition;
        for (var i = 0; i < 60; i++) physics.Update(1f / 30);
        Assert.Multiple(() =>
        {
            Assert.That(body.LocalCenter, Is.Not.EqualTo(Vector2.Zero));
            Assert.That(xform.LocalPosition, Is.Not.EqualTo(position), "Rotation about an offset COM must move the origin.");
            Assert.That(Vector2.Distance(Center(), center), Is.LessThan(0.0002f));
            Assert.That(body.AngularVelocity, Is.EqualTo(2));
        });
    }

    [Test]
    public void RejectedAnchoredPoseDoesNotRaiseMoveOrDirtyState()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var timing = sim.Resolve<IGameTiming>();
        Mock.Get(timing).SetupProperty(t => t.CurTick, GameTick.First);
        var maps = entities.System<SharedMapSystem>();
        var grid = maps.CreateGridEntity(sim.CreateMap().MapId);
        maps.SetTile(grid, Vector2i.Zero, new Tile(1));
        var uid = entities.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
        var transforms = entities.System<SharedTransformSystem>();
        transforms.AnchorEntity(uid);
        transforms.SetNoLocalRotation(uid, true);
        var xform = entities.GetComponent<TransformComponent>(uid);
        var original = xform.LocalPosition;
        var fromTick = timing.CurTick + 1;
        timing.CurTick = fromTick + 1;
        var moves = 0;
        void OnMove(ref MoveEvent ev) { if (ev.Sender == uid) moves++; }
        transforms.OnGlobalMoveEvent += OnMove;
        try { transforms.SetLocalPositionRotation(uid, new Vector2(5, 5), Angle.FromDegrees(90), xform); }
        finally { transforms.OnGlobalMoveEvent -= OnMove; }
        Assert.Multiple(() =>
        {
            Assert.That(xform.Anchored, Is.True);
            Assert.That(xform.LocalPosition, Is.EqualTo(original));
            Assert.That(xform.LocalRotation, Is.EqualTo(Angle.Zero));
            Assert.That(moves, Is.Zero);
            Assert.That(EntityManager.GetModifiedAspects(xform, fromTick), Is.Zero);
        });
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void SolverUsesDeltaAndPreservesReentrantFieldChanges(bool rotate, bool changeDuringMove)
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var timing = sim.Resolve<IGameTiming>();
        Mock.Get(timing).SetupProperty(t => t.CurTick, GameTick.First);
        var cfg = sim.Resolve<IConfigurationManager>();
        cfg.SetCVar(CVars.NetTickrate, 30);
        cfg.SetCVar(CVars.TargetMinimumTickrate, 30);
        var uid = entities.SpawnEntity(null, new MapCoordinates(Vector2.One, sim.CreateMap().MapId));
        var body = entities.AddComponent<PhysicsComponent>(uid);
        entities.System<FixtureSystem>().CreateFixture(uid, "body",
            new Fixture(new PhysShapeCircle(0.1f), 1, 1, true), body: body);
        var physics = entities.System<SharedPhysicsSystem>();
        physics.SetBodyType(uid, BodyType.Dynamic, body: body);
        physics.SetFixedRotation(uid, false, body: body);
        physics.SetLinearDamping(uid, body, 3);
        physics.SetAngularDamping(uid, body, 3);
        physics.SetLinearVelocity(uid, Vector2.UnitX, body: body);
        physics.SetAngularVelocity(uid, rotate ? 2 : 0, body: body);
        // Finish fixture/body initialization before measuring a steady-state tick.
        physics.Update(1f / 30);
        physics.SetLinearVelocity(uid, Vector2.UnitX, body: body);
        physics.SetAngularVelocity(uid, rotate ? 2 : 0, body: body);
        var fromTick = timing.CurTick + 1;
        timing.CurTick = fromTick + 1;
        var transforms = entities.System<SharedTransformSystem>();
        var changed = false;
        void OnMove(ref MoveEvent ev)
        {
            if (ev.Sender != uid || !changeDuringMove || changed) return;
            changed = true;
            physics.SetSleepingAllowed(uid, body, false);
        }
        transforms.OnGlobalMoveEvent += OnMove;
        try { physics.Update(1f / 30); }
        finally { transforms.OnGlobalMoveEvent -= OnMove; }
        var state = entities.GetComponentState(entities.EventBus, body, null, fromTick);
        Assert.That(state, Is.TypeOf<PhysicsComponentDeltaState>());
        var delta = (PhysicsComponentDeltaState) state!;
        var expected = (1UL << PhysicsComponentDeltaState.LinearVelocityIndex)
            | (rotate ? 1UL << PhysicsComponentDeltaState.AngularVelocityIndex : 0UL)
            | (changeDuringMove ? 1UL << PhysicsComponentDeltaState.SleepingAllowedIndex : 0UL);
        Assert.Multiple(() =>
        {
            Assert.That(delta.ChangedFields, Is.EqualTo(expected));
            Assert.That(delta.LinearVelocity, Is.EqualTo(body.LinearVelocity));
            Assert.That(body.LinearVelocity.X, Is.InRange(0.01f, 0.99f), "Damping must still be simulated.");
            Assert.That(changed, Is.EqualTo(changeDuringMove));
            if (rotate) Assert.That(delta.AngularVelocity, Is.EqualTo(body.AngularVelocity));
            if (changeDuringMove) Assert.That(delta.SleepingAllowed, Is.False);
        });
    }
}
