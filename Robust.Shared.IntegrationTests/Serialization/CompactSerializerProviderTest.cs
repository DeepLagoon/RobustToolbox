using System;
using System.Reflection;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Robust.UnitTesting.Shared.Serialization;

[TestFixture]
public sealed class CompactSerializerProviderTest : OurSerializationTest
{
    private static readonly FieldInfo NodeArray = typeof(SerializationManager.SerializerProvider)
        .GetField("_typeNodeSerializersArray", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo TypeArray = typeof(SerializationManager.SerializerProvider)
        .GetField("_typeSerializersArray", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [TestCase(true)]
    [TestCase(false)]
    public void GenericRegularAndLateRegistrationPreserveNativeResolution(bool indexed)
    {
        var provider = new SerializationManager.SerializerProvider(Serialization, indexed);
        Assert.That(provider.TryGetTypeNodeSerializer<ITypeReader<Probe<int>, ValueDataNode>, Probe<int>, ValueDataNode>(out _), Is.False);
        provider.RegisterSerializer(typeof(ProbeSerializer<>));
        var generic = provider.GetTypeNodeSerializer<ITypeReader<Probe<int>, ValueDataNode>, Probe<int>, ValueDataNode>();
        Assert.That(generic.Read(Serialization, new ValueDataNode("42"), null!, SerializationHookContext.DontSkipHooks).Value, Is.EqualTo(42));
        var regular = new RegularProbeSerializer();
        provider.RegisterSerializer(regular);
        Assert.That(provider.GetTypeNodeSerializer<ITypeReader<Probe<int>, ValueDataNode>, Probe<int>, ValueDataNode>(), Is.SameAs(regular),
            "Late regular registration must supersede a materialized generic, including an earlier negative lookup.");
        Assert.That(provider.GetTypeNodeSerializer<ITypeValidator<Probe<int>, ValueDataNode>, Probe<int>, ValueDataNode>(), Is.SameAs(regular));
        Assert.That(provider.GetTypeNodeSerializer<ITypeValidator<Probe<int>, ValueDataNode>, Probe<int>, ValueDataNode>()
            .Validate(Serialization, new ValueDataNode("42"), null!), Is.TypeOf<ValidatedValueNode>());
        Assert.That(provider.TryGetCopierOrCreator<Probe<int>>(out var copier, out var creator), Is.True);
        Assert.That(copier, Is.SameAs(regular));
        Assert.That(creator, Is.SameAs(regular));
        var target = new Probe<int>();
        copier!.CopyTo(Serialization, new() { Value = 7 }, ref target, null!, SerializationHookContext.DontSkipHooks);
        Assert.That(target.Value, Is.EqualTo(7));
        Assert.That(creator!.CreateCopy(Serialization, new() { Value = 9 }, null!, SerializationHookContext.DontSkipHooks).Value, Is.EqualTo(9));

        // With regular registered first, the normal public manager path must honor context
        // writers/readers/validators rather than its global serializer set.
        var context = new Context(new SerializationManager.SerializerProvider(Serialization, indexed));
        context.SerializerProvider.RegisterSerializer(regular);
        context.SerializerProvider.RegisterSerializer(typeof(ProbeSerializer<>));
        var node = (ValueDataNode) Serialization.WriteValue(new Probe<int> { Value = 12 }, context: context, notNullableOverride: true);
        Assert.That(node.Value, Is.EqualTo("12"));
        Assert.That(Serialization.Read<Probe<int>>(node, context: context, notNullableOverride: true).Value, Is.EqualTo(12));
        Assert.That(context.SerializerProvider.GetTypeSerializer<ITypeWriter<Probe<int>>, Probe<int>>(), Is.SameAs(regular));
        if (!indexed)
        {
            Assert.That(((Array) NodeArray.GetValue(provider)!).Length, Is.Zero);
            Assert.That(((Array) TypeArray.GetValue(provider)!).Length, Is.Zero);
        }
    }

    [Test]
    public void SparseContextDoesNotAllocateArraysForHighProcessGlobalTypeIds()
    {
        var padding = new SerializationManager.SerializerProvider(Serialization);
        var type = typeof(Padding);
        for (var i = 0; i < 64; i++)
        {
            padding.RegisterSerializer(typeof(ProbeSerializer<>).MakeGenericType(type));
            type = typeof(Probe<>).MakeGenericType(type);
        }
        var serializer = new ProbeSerializer<LateType>();
        long Create(bool indexed)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var provider = new SerializationManager.SerializerProvider(Serialization, indexed);
            provider.RegisterSerializer(serializer);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(provider.GetTypeNodeSerializer<ITypeReader<Probe<LateType>, ValueDataNode>, Probe<LateType>, ValueDataNode>(), Is.SameAs(serializer));
            if (indexed)
            {
                Assert.That(((Array) NodeArray.GetValue(provider)!).Length, Is.GreaterThan(64));
                Assert.That(((Array) TypeArray.GetValue(provider)!).Length, Is.GreaterThan(64));
            }
            else
            {
                Assert.That(((Array) NodeArray.GetValue(provider)!).Length, Is.Zero);
                Assert.That(((Array) TypeArray.GetValue(provider)!).Length, Is.Zero);
            }
            return bytes;
        }
        Create(true); Create(false); // Warm reflection/type initializers before allocation comparison.
        var dense = Create(true);
        var compact = Create(false);
        Assert.That(compact, Is.LessThan(dense), "Sparse process-global IDs must not allocate a dense per-snapshot table.");
        TestContext.WriteLine($"provider indexed_thread_bytes={dense} compact_thread_bytes={compact}");
    }

    private sealed class Context(SerializationManager.SerializerProvider provider) : ISerializationContext
    {
        public SerializationManager.SerializerProvider SerializerProvider { get; } = provider;
        public bool WritingReadingPrototypes => false;
    }
    public sealed class Probe<T> { public int Value; }
    public sealed class Padding { }
    public sealed class LateType { }
    public sealed class RegularProbeSerializer : ProbeSerializer<int> { }
    public class ProbeSerializer<T> : ITypeSerializer<Probe<T>, ValueDataNode>, ITypeCopier<Probe<T>>, ITypeCopyCreator<Probe<T>>
    {
        public Probe<T> Read(ISerializationManager serializationManager, ValueDataNode node, IDependencyCollection dependencies,
            SerializationHookContext hookCtx, ISerializationContext? context = null, ISerializationManager.InstantiationDelegate<Probe<T>>? instanceProvider = null)
            => new() { Value = int.Parse(node.Value) };
        public ValidationNode Validate(ISerializationManager serializationManager, ValueDataNode node, IDependencyCollection dependencies,
            ISerializationContext? context = null) => new ValidatedValueNode(node);
        public DataNode Write(ISerializationManager serializationManager, Probe<T> value, IDependencyCollection dependencies,
            bool alwaysWrite = false, ISerializationContext? context = null) => new ValueDataNode(value.Value.ToString());
        public void CopyTo(ISerializationManager serializationManager, Probe<T> source, ref Probe<T> target, IDependencyCollection dependencies,
            SerializationHookContext hookCtx, ISerializationContext? context = null) => target.Value = source.Value;
        public Probe<T> CreateCopy(ISerializationManager serializationManager, Probe<T> source, IDependencyCollection dependencies,
            SerializationHookContext hookCtx, ISerializationContext? context = null) => new() { Value = source.Value };
    }
}
