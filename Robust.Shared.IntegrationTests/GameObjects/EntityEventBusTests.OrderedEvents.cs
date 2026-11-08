using System.Collections.Generic;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Reflection;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.GameObjects;

internal sealed partial class EntityEventBusTests
{
    // Explanation of what bug this is testing:
    // Because event ordering is keyed on system type, we have a problem.
    // If you register to a directed event like FooEvent twice for different components,
    // you now have different subscriptions with the same key.
    //
    // To trigger this, at least one subscription to this event (possibly another system entirely)
    // needs to demand some ordering calculation to happen.

    [Test]
    public void TestDifferentComponentsOrderedSameKeySub()
    {
        var simulation = RobustServerSimulation
            .NewSimulation()
            .RegisterEntitySystems(factory =>
            {
                factory.LoadExtraSystemType<DifferentComponentsSameKeySubSystem>();
                factory.LoadExtraSystemType<DifferentComponentsSameKeySubSystem2>();
            })
            .RegisterComponents(factory => factory.RegisterClass<FooComponent>())
            .InitializeInstance();

        var map = simulation.CreateMap().MapId;
        var entMan = simulation.Resolve<IEntityManager>();

        var entity = entMan.Spawn(null, new MapCoordinates(0, 0, map));
        entMan.AddComponent<FooComponent>(entity);

        var foo = new FooEvent();
        entMan.EventBus.RaiseLocalEvent(entity, foo, true);

        Assert.That(foo.EventOrder, Is.EquivalentTo(new[]{"Foo", "Transform", "Metadata"}).Or.EquivalentTo(new[]{"Foo", "Metadata", "Transform"}));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OrderedDispatchHandlesNestedRaiseAndComponentChange(bool replaceComponent)
    {
        var simulation = RobustServerSimulation
            .NewSimulation()
            .RegisterEntitySystems(factory =>
            {
                factory.LoadExtraSystemType<OrderedDispatchStartSystem>();
                factory.LoadExtraSystemType<OrderedDispatchEndSystem>();
            })
            .RegisterComponents(factory => factory.RegisterClass<FooComponent>())
            .InitializeInstance();

        var map = simulation.CreateMap().MapId;
        var entMan = simulation.Resolve<IEntityManager>();
        var entity = entMan.Spawn(null, new MapCoordinates(0, 0, map));
        entMan.AddComponent<FooComponent>(entity);

        var calls = new List<string>();
        var ev = new NestedOrderedEvent(entity, calls, replaceComponent, false);
        entMan.EventBus.RaiseLocalEvent(entity, ref ev, true);

        Assert.That(calls, Is.EqualTo(replaceComponent
            ? new[] { "outer broadcast", "nested broadcast", "nested replacement", "outer replacement" }
            : new[] { "outer broadcast", "nested broadcast" }));
    }

    [Reflect(false)]
    private sealed class OrderedDispatchStartSystem : EntitySystem
    {
        public override void Initialize()
        {
            SubscribeLocalEvent<NestedOrderedEvent>(OnEvent, before: new[] { typeof(OrderedDispatchEndSystem) });
        }

        private void OnEvent(ref NestedOrderedEvent ev)
        {
            ev.Calls.Add(ev.Nested ? "nested broadcast" : "outer broadcast");
            if (ev.Nested)
                return;

            EntityManager.RemoveComponent<FooComponent>(ev.Target);
            if (ev.ReplaceComponent)
                EntityManager.AddComponent<FooComponent>(ev.Target).Replacement = true;

            var nested = ev with { Nested = true };
            RaiseLocalEvent(ev.Target, ref nested, true);
        }
    }

    [Reflect(false)]
    private sealed class OrderedDispatchEndSystem : EntitySystem
    {
        public override void Initialize()
        {
            SubscribeLocalEvent<FooComponent, NestedOrderedEvent>(OnEvent, after: new[] { typeof(OrderedDispatchStartSystem) });
        }

        private static void OnEvent(EntityUid uid, FooComponent component, ref NestedOrderedEvent ev)
        {
            Assert.That(component.Replacement, Is.True, "The old component must not receive the event.");
            ev.Calls.Add(ev.Nested ? "nested replacement" : "outer replacement");
        }
    }

    [ByRefEvent]
    private record struct NestedOrderedEvent(EntityUid Target, List<string> Calls, bool ReplaceComponent, bool Nested);

    [Reflect(false)]
    private sealed class DifferentComponentsSameKeySubSystem : EntitySystem
    {
        public override void Initialize()
        {
            SubscribeLocalEvent<TransformComponent, FooEvent>((_, _, e) => { e.EventOrder.Add("Transform"); });
            SubscribeLocalEvent<MetaDataComponent, FooEvent>((_, _, e) => { e.EventOrder.Add("Metadata"); });
        }
    }

    [Reflect(false)]
    private sealed class DifferentComponentsSameKeySubSystem2 : EntitySystem
    {
        public override void Initialize()
        {
            SubscribeLocalEvent<FooComponent, FooEvent>(
                (_, _, e) => e.EventOrder.Add("Foo"),
                before: new[] {typeof(DifferentComponentsSameKeySubSystem)});
        }
    }

    [Reflect(false)]
    private sealed partial class FooComponent : Component
    {
        public bool Replacement;
    }

    private sealed class FooEvent
    {
        public List<string> EventOrder = new();
    }
}
