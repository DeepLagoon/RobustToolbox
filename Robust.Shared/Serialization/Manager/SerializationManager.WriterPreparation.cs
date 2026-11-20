using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Clock = System.Diagnostics.Stopwatch;

namespace Robust.Shared.Serialization.Manager;

public sealed partial class SerializationManager
{
    private readonly ConcurrentDictionary<Type, Task<PreparedWriterType>> _preparingWriters = new();
    // Cache only complete, successful static metadata closures. CLR Type keys are immutable;
    // no entity, prototype instance, DataNode or mutable editor state is retained here.
    private const int MaximumPreparedRootTypes = 1024;
    private readonly ConcurrentDictionary<Type, byte> _preparedWriterRoots = new();
    private readonly object _preparedRootGate = new();
    private readonly record struct PreparedWriterType(int Expressions, double QueueMs, double CompileMs, int WorkerThread, bool WorkerContext);
    private sealed record WriterPlan(Type Type, bool NotNullable, bool Boxing, LambdaExpression Expression);
    private sealed record WriterClaim(Type Type, TaskCompletionSource<PreparedWriterType> Completion, int Start, int Count);

    /// <summary>
    /// Resolve metadata and build expression plans on the calling thread. The worker only
    /// compiles those expressions; it never invokes a writer, serializer, ECS or prototype.
    /// Call from the game thread when preparing live entities. Normal writes remain a correct
    /// synchronous fallback for new runtime/polymorphic types not present in these plans.
    /// Existing public writer/constructor interfaces are unchanged.
    /// </summary>
    public Task<WriterPreparationResult> PrepareWritersAsync(IReadOnlyCollection<Type> componentTypes)
        => PrepareWritersAsync(componentTypes, 512);

    internal Task<WriterPreparationResult> PrepareWritersAsync(IReadOnlyCollection<Type> componentTypes, int maximumTypes)
    {
        var started = Clock.GetTimestamp();
        if (maximumTypes < 1)
            return Task.FromException<WriterPreparationResult>(new ArgumentOutOfRangeException(nameof(maximumTypes)));
        HashSet<Type>? roots = null;
        foreach (var type in componentTypes)
            if (!_preparedWriterRoots.ContainsKey(type)) (roots ??= new()).Add(type);
        if (roots == null)
            return Task.FromResult(new WriterPreparationResult(componentTypes.Count, 0, 0, 0,
                Clock.GetElapsedTime(started).TotalMilliseconds, 0, 0, 0, false));
        var pending = new Queue<Type>(roots);
        var visited = new HashSet<Type>();
        var types = new List<Type>();
        var deferred = 0;
        while (pending.TryDequeue(out var type))
        {
            if (visited.Contains(type)) continue;
            if (_preparedWriterRoots.ContainsKey(type)) { visited.Add(type); continue; }
            if (type == typeof(void) || type.IsByRef || type.IsPointer || type.IsByRefLike || type.ContainsGenericParameters)
                return Task.FromException<WriterPreparationResult>(new ArgumentException($"Cannot prepare writer for {type}.", nameof(componentTypes)));
            if (types.Count == maximumTypes)
            {
                // The just-popped type is deferred even if it was the last queue entry.
                deferred = 1 + pending.Count;
                break;
            }
            visited.Add(type);
            if (!type.IsAbstract && !type.IsInterface) types.Add(type);
            // Scoped metadata closure only: no default/prototype/component instances are read.
            if (GetDefinition(type) is { } definition)
                foreach (var field in definition.BaseFieldDefinitions)
                    if ((field.IsDataField || field.IsIncludeDataField) && field.CustomTypeSerializer == null)
                        pending.Enqueue(field.FieldType);
            if (type.IsArray) pending.Enqueue(type.GetElementType()!);
            if (type.IsGenericType)
                foreach (var argument in type.GetGenericArguments()) pending.Enqueue(argument);
        }

        var plans = new List<WriterPlan>();
        var claims = new List<WriterClaim>();
        var waiting = new List<Task<PreparedWriterType>>();
        foreach (var type in types)
        {
            if (HasPreparedWriter(type, false) && HasPreparedWriter(type, true)) continue;
            var receipt = new TaskCompletionSource<PreparedWriterType>(TaskCreationOptions.RunContinuationsAsynchronously);
            var existing = _preparingWriters.GetOrAdd(type, receipt.Task);
            if (!ReferenceEquals(existing, receipt.Task))
            {
                waiting.Add(existing);
                continue;
            }
            waiting.Add(receipt.Task);
            var start = plans.Count;
            try
            {
                foreach (var notNullable in new[] { false, true })
                {
                    if (!_writeBoxingDelegates.ContainsKey((type, notNullable)))
                        plans.Add(new(type, notNullable, true, BuildWriteBoxingExpression(type, notNullable)));
                    if (!HasPreparedGenericWriter(type, notNullable))
                        plans.Add(new(type, notNullable, false, BuildWriteGenericExpression(type, type, notNullable)));
                }
                claims.Add(new(type, receipt, start, plans.Count - start));
            }
            catch (Exception error)
            {
                plans.RemoveRange(start, plans.Count - start);
                receipt.SetException(error);
                _preparingWriters.TryRemove(type, out _);
            }
        }
        return CompleteWriterPreparationAsync(componentTypes.Count, deferred, visited.Count,
            Clock.GetElapsedTime(started).TotalMilliseconds, roots, plans, claims, waiting);
    }

    private bool HasPreparedWriter(Type type, bool notNullable)
        => _writeBoxingDelegates.ContainsKey((type, notNullable)) && HasPreparedGenericWriter(type, notNullable);
    private bool HasPreparedGenericWriter(Type type, bool notNullable)
        => type.IsSealed ? _writeGenericDelegates.ContainsKey((type, notNullable))
            : _writeGenericBaseDelegates.ContainsKey((type, type, notNullable));

    private async Task<WriterPreparationResult> CompleteWriterPreparationAsync(int requested, int deferred, int metadataVisited,
        double planningMs, HashSet<Type> roots, List<WriterPlan> plans, List<WriterClaim> claims, List<Task<PreparedWriterType>> waiting)
    {
        if (claims.Count > 0)
        {
            var queued = Clock.GetTimestamp();
            try
            {
                // Only LambdaExpression.Compile executes on this worker. No service lookup,
                // writer invocation, metadata resolution, or instance access occurs here.
                var compiled = await Task.Run(() =>
                {
                    var workerStarted = Clock.GetTimestamp();
                    var output = new Delegate[plans.Count];
                    var durations = new double[plans.Count];
                    for (var i = 0; i < plans.Count; i++)
                    {
                        var at = Clock.GetTimestamp();
                        output[i] = plans[i].Expression.Compile();
                        durations[i] = Clock.GetElapsedTime(at).TotalMilliseconds;
                    }
                    return (output, durations, QueueMs: Clock.GetElapsedTime(queued, workerStarted).TotalMilliseconds,
                        Thread: Environment.CurrentManagedThreadId, Context: SynchronizationContext.Current != null);
                });
                for (var i = 0; i < plans.Count; i++)
                {
                    var plan = plans[i];
                    if (plan.Boxing) _writeBoxingDelegates.TryAdd((plan.Type, plan.NotNullable), (WriteBoxingDelegate) compiled.output[i]);
                    else if (plan.Type.IsSealed) _writeGenericDelegates.TryAdd((plan.Type, plan.NotNullable), compiled.output[i]);
                    else _writeGenericBaseDelegates.TryAdd((plan.Type, plan.Type, plan.NotNullable), compiled.output[i]);
                }
                foreach (var claim in claims)
                {
                    var compileMs = 0d;
                    for (var i = claim.Start; i < claim.Start + claim.Count; i++) compileMs += compiled.durations[i];
                    claim.Completion.SetResult(new(claim.Count, compiled.QueueMs, compileMs, compiled.Thread, compiled.Context));
                }
            }
            catch (Exception error)
            {
                foreach (var claim in claims) claim.Completion.TrySetException(error);
            }
            finally
            {
                foreach (var claim in claims) _preparingWriters.TryRemove(claim.Type, out _);
            }
        }
        var results = await Task.WhenAll(waiting);
        if (deferred == 0)
        {
            lock (_preparedRootGate)
                foreach (var root in roots)
                {
                    if (_preparedWriterRoots.Count >= MaximumPreparedRootTypes) break;
                    _preparedWriterRoots.TryAdd(root, 0);
                }
        }
        return new(requested, results.Length, deferred, results.Sum(x => x.Expressions), planningMs,
            results.Length == 0 ? 0 : results.Max(x => x.QueueMs), results.Sum(x => x.CompileMs),
            results.FirstOrDefault(x => x.Expressions > 0).WorkerThread, results.Any(x => x.WorkerContext))
            { MetadataTypesVisited = metadataVisited };
    }
}

public readonly record struct WriterPreparationResult(int RequestedTypes, int PreparedTypes, int DeferredTypes,
    int ExpressionsCompiled, double PlanningMilliseconds, double QueueMilliseconds, double CompileMilliseconds,
    int WorkerThreadId, bool WorkerHadSynchronizationContext)
{
    public int MetadataTypesVisited { get; init; }
}
