using NUnit.Framework;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Robust.UnitTesting.Shared.Serialization.SerializationTests;

internal sealed partial class GeneratedDefinitionReadingTests : OurSerializationTest
{
    [DataDefinition]
    internal sealed partial class HookedDefinition : ISerializationHooks
    {
        [DataField] public int Value = 7;
        public int HookCalls;

        void ISerializationHooks.AfterDeserialization() => HookCalls++;
    }

    [DataDefinition]
    internal partial struct ValueDefinition
    {
        [DataField] public int Value;
    }

    [Test]
    public void MappingRetainsProviderInstanceAndRunsHookOnce()
    {
        var instance = new HookedDefinition();
        var result = ((SerializationManager)Serialization).ReadDefinition(
            new MappingDataNode { { "value", "42" } },
            SerializationHookContext.DontSkipHooks,
            instanceProvider: () => instance);

        Assert.That(result, Is.SameAs(instance));
        Assert.That(result.Value, Is.EqualTo(42));
        Assert.That(result.HookCalls, Is.EqualTo(1));
    }

    [Test]
    public void EmptyValueCreatesDefinitionAndHonorsSkipHooks()
    {
        var manager = (SerializationManager)Serialization;
        var result = manager.ReadDefinition<HookedDefinition>(new ValueDataNode(""), SerializationHookContext.DontSkipHooks);
        Assert.That(result.Value, Is.EqualTo(7));
        Assert.That(result.HookCalls, Is.EqualTo(1));

        var skipped = manager.ReadDefinition<HookedDefinition>(new ValueDataNode(""), SerializationHookContext.DoSkipHooks);
        Assert.That(skipped.HookCalls, Is.Zero);
    }

    [Test]
    public void StructMappingPopulatesReturnedValue()
    {
        var result = ((SerializationManager)Serialization).ReadStructDefinition<ValueDefinition>(
            new MappingDataNode { { "value", "42" } }, SerializationHookContext.DontSkipHooks);
        Assert.That(result.Value, Is.EqualTo(42));
    }
}
