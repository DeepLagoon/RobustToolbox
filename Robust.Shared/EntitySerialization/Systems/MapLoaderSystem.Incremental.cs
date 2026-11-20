using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Utility;
using Clock = System.Diagnostics.Stopwatch;

namespace Robust.Shared.EntitySerialization.Systems;

public sealed partial class MapLoaderSystem
{
    /// <summary>
    /// Opt-in loading of a pre-mapinit grid onto a private, uninitialized paused map.
    /// Advance on the game thread. Only file reading/YAML parsing runs on a worker.
    /// The caller owns cancellation cleanup of <see cref="IncrementalGridLoad.Entities"/>.
    /// </summary>
    public IncrementalGridLoad BeginIncrementalGridLoad(MapId stagingMap, ResPath file,
        DeserializationOptions? options = null) => new(this, stagingMap, file, options ?? DeserializationOptions.Default);

    public sealed class IncrementalGridLoad : IDisposable
    {
        private readonly MapLoaderSystem _loader;
        private readonly MapId _map;
        private readonly ResPath _file;
        private readonly DeserializationOptions _options;
        private readonly IEnumerator<GridLoadPhase> _steps;
        private EntityDeserializer? _deserializer;
        private readonly long _started = Clock.GetTimestamp();
        private readonly long[] _phaseTicks = new long[8];
        private bool _disposed;
        public GridLoadPhase Phase { get; private set; }
        public bool Completed { get; private set; }
        public bool RequiresSynchronousLoad { get; private set; }
        public Entity<MapGridComponent>? Grid { get; private set; }
        public IReadOnlyCollection<EntityUid> Entities => _deserializer?.Result.Entities ?? EmptyEntities;
        private static readonly HashSet<EntityUid> EmptyEntities = new();
        public double MaximumStepMilliseconds { get; private set; }
        public GridLoadPhase MaximumStepPhase { get; private set; }
        public double MaximumStepGcPauseMilliseconds { get; private set; }
        public TimeSpan ReadTime { get; private set; }
        public double CpuMilliseconds => _phaseTicks.Sum() * 1000.0 / Clock.Frequency;
        public TimeSpan GetPhaseTime(GridLoadPhase phase) => TimeSpan.FromSeconds((double) _phaseTicks[(int) phase] / Clock.Frequency);

        internal IncrementalGridLoad(MapLoaderSystem loader, MapId map, ResPath file, DeserializationOptions options)
        {
            if (!loader._mapSystem.MapExists(map) || !loader._mapSystem.IsPaused(map) || loader._mapSystem.IsInitialized(map))
                throw new ArgumentException("Incremental grid loads require a private paused pre-mapinit staging map.");
            _loader = loader;
            _map = map;
            _file = file;
            _options = options;
            _steps = Load().GetEnumerator();
        }

        public bool MoveNext()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Completed) return false;
            var gcBefore = GC.GetTotalPauseDuration();
            var start = Clock.GetTimestamp();
            var more = _steps.MoveNext();
            var elapsed = Clock.GetTimestamp() - start;
            Phase = more ? _steps.Current : GridLoadPhase.Finalize;
            _phaseTicks[(int) Phase] += elapsed;
            var milliseconds = elapsed * 1000.0 / Clock.Frequency;
            if (milliseconds > MaximumStepMilliseconds)
            {
                MaximumStepMilliseconds = milliseconds;
                MaximumStepPhase = Phase;
                MaximumStepGcPauseMilliseconds = (GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds;
            }
            if (more) return true;
            Completed = true;
            if (Grid != null && _options.ReportMapLoadTimings is { } report)
            {
                TimeSpan At(GridLoadPhase phase) => TimeSpan.FromSeconds((double) _phaseTicks[(int) phase] / Clock.Frequency);
                report(new(Clock.GetElapsedTime(_started), ReadTime,
                    At(GridLoadPhase.Prepare) + At(GridLoadPhase.Hierarchy) + At(GridLoadPhase.Finalize),
                    At(GridLoadPhase.Allocate) + At(GridLoadPhase.Components),
                    At(GridLoadPhase.Startup) + At(GridLoadPhase.MapInit), Entities.Count));
            }
            return false;
        }

        private IEnumerable<GridLoadPhase> Load()
        {
            if (!_loader.TryGetReader(_file.ToRootedPath(), out var reader))
                yield break;
            // Resource selection/opening is on the game thread; the worker owns only this
            // stream and its isolated YAML document, never ECS or prototype services.
            var read = Task.Run(() =>
            {
                var start = Clock.GetTimestamp();
                using (reader)
                {
                    var documents = DataNodeParser.ParseYamlStream(reader).ToArray();
                    if (documents.Length != 1) throw new InvalidDataException("Expected one YAML document.");
                    return (Data: (MappingDataNode) documents[0].Root, Time: Clock.GetElapsedTime(start));
                }
            });
            _ = read.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            while (!read.IsCompleted) yield return GridLoadPhase.Read;
            var parsed = read.GetAwaiter().GetResult();
            ReadTime = parsed.Time;
            var ev = new BeforeEntityReadEvent();
            _loader.RaiseLocalEvent(ev);
            _deserializer = new(_loader._dependency, parsed.Data, _options, ev.RenamedPrototypes, ev.DeletedPrototypes);
            if (!_deserializer.TryProcessData()) yield break;
            // Saved live entities have offset clocks and must retain the existing synchronous
            // contract. No entities have been allocated when this fallback is requested.
            if (_deserializer.YamlEntities.Values.Any(e => e.PostInit))
            {
                RequiresSynchronousLoad = true;
                yield break;
            }
            yield return GridLoadPhase.Prepare;
            foreach (var phase in _deserializer.CreateEntitySteps()) yield return phase;
            if (_deserializer.Result.Category != FileCategory.Grid || _deserializer.Result.Grids.Count != 1)
                throw new InvalidDataException($"{_file} is not a single-grid file.");
            var merged = new HashSet<EntityUid>();
            _loader.MergeMaps(_deserializer, new MapLoadOptions { MergeMap = _map }, merged);
            yield return GridLoadPhase.Prepare;
            foreach (var phase in _deserializer.StartEntitySteps()) yield return phase;
            // Map-init callbacks can spawn inventories, NPCs and loot. Spread those too,
            // keeping the staging map paused until publication by the owning content system.
            foreach (var _ in _loader._mapSystem.InitializeMapSteps(_loader._mapSystem.GetMap(_map)))
                yield return GridLoadPhase.MapInit;
            Grid = _deserializer.Result.Grids.Single();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _steps.Dispose();
        }
    }
}
