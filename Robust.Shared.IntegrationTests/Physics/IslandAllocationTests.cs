using System;
using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

[TestFixture]
public sealed class IslandAllocationTests
{
    [Test]
    public void IsolatedAwakeBodiesReuseIslandStorage()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var physics = entities.System<SharedPhysicsSystem>();
        var fixtures = entities.System<FixtureSystem>();
        var map = sim.CreateMap();

        // Bodies on separate islands are merged into the solver's lone island.
        // They must not require three fresh lists per body on every substep.
        const int count = 256;
        for (var i = 0; i < count; i++)
        {
            var uid = entities.SpawnEntity(null, new MapCoordinates(new Vector2(i * 4, 0), map.MapId));
            var body = entities.AddComponent<PhysicsComponent>(uid);
            fixtures.CreateFixture(uid, "body", new Fixture(new PhysShapeCircle(0.25f), 1, 1, true), body: body);
            physics.SetBodyType(uid, BodyType.Dynamic, body: body);
            physics.SetSleepingAllowed(uid, body, false);
            physics.WakeBody(uid, body: body);
        }

        for (var i = 0; i < 8; i++)
            physics.Step(1f / 60, false);

        const int steps = 16;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < steps; i++)
            physics.Step(1f / 60, false);
        var bytesPerStep = (GC.GetAllocatedBytesForCurrentThread() - before) / steps;
        TestContext.Out.WriteLine($"Allocated bytes per warmed step: {bytesPerStep}");

        Assert.That(bytesPerStep, Is.LessThan(32_768),
            "Warmed isolated bodies must reuse island lists rather than allocate per body and substep.");
    }
}
