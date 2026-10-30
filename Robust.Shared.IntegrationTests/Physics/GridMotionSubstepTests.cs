using System;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

[TestFixture]
public sealed class GridMotionSubstepTests
{
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
