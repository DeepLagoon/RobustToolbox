using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Robust.UnitTesting.Shared.Prototypes;

[TestFixture]
internal sealed class PrototypeReloadTests : OurRobustUnitTest
{
    private IPrototypeManager _prototypes = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        IoCManager.Resolve<ISerializationManager>().Initialize();
        _prototypes = IoCManager.Resolve<IPrototypeManager>();
        _prototypes.Initialize();
    }

    [Test]
    public void ReloadComposesParentLoadedOutsideTheChangedSet()
    {
        _prototypes.LoadString("""
            - type: entity
              id: HotReloadChild
              name: old child
            """);
        _prototypes.ResolveResults();
        // Loading a new file populates raw mappings, not composed mappings.
        _prototypes.LoadString("""
            - type: entity
              id: ColMarTechBase
              abstract: true
              name: inherited vendor
            """);
        var changed = new Dictionary<Type, HashSet<string>>();
        _prototypes.LoadString("""
            - type: entity
              id: HotReloadChild
              parent: ColMarTechBase
            """, overwrite: true, changed: changed);
        _prototypes.ReloadPrototypes(changed);
        Assert.That(_prototypes.Index<EntityPrototype>("HotReloadChild").Name, Is.EqualTo("inherited vendor"));
    }

    [Test]
    public void ReloadComposesMissingAncestorsForMultipleParents()
    {
        _prototypes.LoadString("""
            - type: entity
              id: HotReloadOtherParent
              abstract: true
              description: other parent description
            """);
        _prototypes.ResolveResults();
        _prototypes.LoadString("""
            - type: entity
              id: HotReloadRoot
              abstract: true
              name: root name
            - type: entity
              id: HotReloadParent
              abstract: true
              parent: HotReloadRoot
            """);
        var changed = new Dictionary<Type, HashSet<string>>();
        _prototypes.LoadString("""
            - type: entity
              id: HotReloadMultipleChild
              parent: [HotReloadParent, HotReloadOtherParent]
            """, changed: changed);
        _prototypes.ReloadPrototypes(changed);
        var child = _prototypes.Index<EntityPrototype>("HotReloadMultipleChild");
        Assert.That(child.Name, Is.EqualTo("root name"));
        Assert.That(child.Description, Is.EqualTo("other parent description"));
    }

    [Test]
    public void MissingParentPreservesLiveChildrenAndRecoversWhenItsFileArrives()
    {
        _prototypes.LoadString("""
            - type: entity
              id: HotReloadDeferredChild
              name: last good name
            - type: entity
              id: HotReloadDeferredGrandchild
              parent: HotReloadDeferredChild
            """);
        _prototypes.ResolveResults();
        var oldChild = _prototypes.Index<EntityPrototype>("HotReloadDeferredChild");
        var oldGrandchild = _prototypes.Index<EntityPrototype>("HotReloadDeferredGrandchild");
        var changed = new Dictionary<Type, HashSet<string>>();
        _prototypes.LoadString("""
            - type: entity
              id: HotReloadDeferredChild
              parent: HotReloadDeferredParent
            - type: entity
              id: HotReloadIndependent
              name: independent change
            """, overwrite: true, changed: changed);
        Assert.DoesNotThrow(() => _prototypes.ReloadPrototypes(changed));
        Assert.That(_prototypes.Index<EntityPrototype>("HotReloadDeferredChild"), Is.SameAs(oldChild));
        Assert.That(_prototypes.Index<EntityPrototype>("HotReloadDeferredGrandchild"), Is.SameAs(oldGrandchild));
        Assert.That(_prototypes.Index<EntityPrototype>("HotReloadIndependent").Name, Is.EqualTo("independent change"));

        changed.Clear();
        _prototypes.LoadString("""
            - type: entity
              id: HotReloadDeferredParent
              abstract: true
              name: recovered name
            """, changed: changed);
        _prototypes.ReloadPrototypes(changed);
        Assert.That(_prototypes.Index<EntityPrototype>("HotReloadDeferredChild").Name, Is.EqualTo("recovered name"));
        Assert.That(_prototypes.Index<EntityPrototype>("HotReloadDeferredGrandchild").Name, Is.EqualTo("recovered name"));
    }
}
