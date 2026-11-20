using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Robust.UnitTesting.Shared.Serialization;

[TestFixture]
public sealed partial class WriterPreparationTest : OurSerializationTest
{
    [Test]
    public async Task ConcurrentPreparationCompilesWithoutInvokingWritersAndPreservesData()
    {
        CheckedScalar.Calls = 0;
        var manager = (SerializationManager) Serialization;
        var first = manager.PrepareWritersAsync(new[] { typeof(PreparedEnvelope) });
        var second = manager.PrepareWritersAsync(new[] { typeof(PreparedEnvelope) });
        var results = await Task.WhenAll(first, second);
        Assert.That(results[0].ExpressionsCompiled, Is.GreaterThan(0));
        Assert.That(results[0].WorkerThreadId, Is.GreaterThan(0));
        Assert.That(results[0].WorkerHadSynchronizationContext, Is.False);
        Assert.That(CheckedScalar.Calls, Is.Zero, "Compiling metadata must not execute any serializer or instance writer.");
        var value = new PreparedEnvelope { Text = "same data", Leaves = new() { new() { Number = 42 } }, Scalar = new() { Value = "checked" } };
        var actual = manager.WriteValueAs<MappingDataNode>(value, alwaysWrite: true);
        var expected = new MappingDataNode {
            { "text", "same data" }, { "leaves", new SequenceDataNode(new MappingDataNode { { "number", "42" } }) }, { "scalar", "checked" } };
        Assert.That(actual.Equals(expected), Is.True);
        Assert.That(CheckedScalar.Calls, Is.GreaterThan(0));
        var warm = await manager.PrepareWritersAsync(new[] { typeof(PreparedEnvelope) });
        Assert.That(warm.ExpressionsCompiled, Is.Zero, "Ready delegates must be reused without a new worker compile batch.");
        Assert.That(warm.MetadataTypesVisited, Is.Zero, "A successful static root must not rebuild its metadata closure.");
        TestContext.WriteLine($"prepare plan_ms={results[0].PlanningMilliseconds:F3} queue_ms={results[0].QueueMilliseconds:F3} compile_ms={results[0].CompileMilliseconds:F3} expressions={results[0].ExpressionsCompiled}");
    }

    [Test]
    public async Task FailedPreparationDoesNotPoisonRecoveryOrNewRuntimeTypeFallback()
    {
        var manager = (SerializationManager) Serialization;
        Assert.ThrowsAsync<ArgumentException>(async () => await manager.PrepareWritersAsync(new[] { typeof(void) }));
        await manager.PrepareWritersAsync(new[] { typeof(RecoveryEnvelope) });
        var normal = manager.WriteValueAs<MappingDataNode>(new RecoveryEnvelope { Text = "durable" }, alwaysWrite: true);
        Assert.That(normal.Get<ValueDataNode>("text").Value, Is.EqualTo("durable"));
        // This type was not in the preparation request or its metadata closure. New fields/
        // components retain the original correct cold serialization path.
        var late = manager.WriteValueAs<MappingDataNode>(new LateEnvelope { Value = 71 }, alwaysWrite: true);
        Assert.That(late.Get<ValueDataNode>("value").Value, Is.EqualTo("71"));
    }

    [Test]
    public async Task SuccessfulRootsSkipMetadataButNewRootsAndFailedRequestsRemainIndependent()
    {
        var manager = (SerializationManager) Serialization;
        var first = await manager.PrepareWritersAsync(new[] { typeof(CachedEnvelope) });
        Assert.That(first.MetadataTypesVisited, Is.GreaterThan(0));
        var warm = await manager.PrepareWritersAsync(new[] { typeof(CachedEnvelope) });
        Assert.That(warm.MetadataTypesVisited, Is.Zero);
        Assert.That(warm.ExpressionsCompiled, Is.Zero);
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await manager.PrepareWritersAsync(new[] { typeof(CachedEnvelope), typeof(void) }));
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await manager.PrepareWritersAsync(new[] { typeof(void) }));
        var afterFailure = await manager.PrepareWritersAsync(new[] { typeof(CachedEnvelope) });
        Assert.That(afterFailure.MetadataTypesVisited, Is.Zero, "Failure must neither cache an invalid root nor evict an already valid root.");
        var added = await manager.PrepareWritersAsync(new[] { typeof(CachedEnvelope), typeof(NewCachedEnvelope) });
        Assert.That(added.MetadataTypesVisited, Is.GreaterThan(0), "A new component/root must still prepare its own static closure.");
        Assert.That(manager.WriteValueAs<MappingDataNode>(new NewCachedEnvelope { Number = 93 }, alwaysWrite: true)
            .Get<ValueDataNode>("number").Value, Is.EqualTo("93"));
        var bothWarm = await manager.PrepareWritersAsync(new[] { typeof(CachedEnvelope), typeof(NewCachedEnvelope) });
        Assert.That(bothWarm.MetadataTypesVisited, Is.Zero);
    }

    [Test]
    public async Task MetadataLimitIncludesPoppedLastEntryAndRetriesBeforeCachingRoot()
    {
        var manager = (SerializationManager) Serialization;
        // With a one-type budget, the root enqueues exactly one child. It is popped
        // when pending.Count becomes zero; the old counter incorrectly called that complete.
        var limited = await manager.PrepareWritersAsync(new[] { typeof(CappedEnvelope) }, maximumTypes: 1);
        Assert.That(limited.DeferredTypes, Is.EqualTo(1));
        Assert.That(limited.MetadataTypesVisited, Is.EqualTo(1));
        var retried = await manager.PrepareWritersAsync(new[] { typeof(CappedEnvelope) });
        Assert.That(retried.DeferredTypes, Is.Zero);
        Assert.That(retried.MetadataTypesVisited, Is.GreaterThan(1), "A truncated closure must not enter the successful-root cache.");
        var warm = await manager.PrepareWritersAsync(new[] { typeof(CappedEnvelope) });
        Assert.That(warm.MetadataTypesVisited, Is.Zero);
        var data = manager.WriteValueAs<MappingDataNode>(new CappedEnvelope { Child = new() { Number = 17 } }, alwaysWrite: true);
        Assert.That(data.Get<MappingDataNode>("child").Get<ValueDataNode>("number").Value, Is.EqualTo("17"));
    }

    [DataDefinition]
    public sealed partial class PreparedEnvelope
    {
        [DataField("text")] public string Text = "";
        [DataField("leaves")] public List<PreparedLeaf> Leaves = new();
        [DataField("scalar")] public CheckedScalar Scalar = new();
    }
    [DataDefinition]
    public sealed partial class PreparedLeaf { [DataField("number")] public int Number; }
    [DataDefinition]
    public sealed partial class RecoveryEnvelope { [DataField("text")] public string Text = ""; }
    [DataDefinition]
    public sealed partial class LateEnvelope { [DataField("value")] public int Value; }
    [DataDefinition]
    public sealed partial class CachedEnvelope { [DataField("text")] public string Text = ""; }
    [DataDefinition]
    public sealed partial class NewCachedEnvelope { [DataField("number")] public int Number; }
    [DataDefinition]
    public sealed partial class CappedEnvelope { [DataField("child")] public CappedLeaf Child = new(); }
    [DataDefinition]
    public sealed partial class CappedLeaf { [DataField("number")] public int Number; }
    public sealed class CheckedScalar : ISelfSerialize
    {
        public static int Calls;
        public string Value = "";
        public void Deserialize(string value) => Value = value;
        public string Serialize() { Interlocked.Increment(ref Calls); return Value; }
    }
}
