using System;
using System.Globalization;
using System.Text;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Timing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Robust.Server;

/// <summary>Samples elapsed time on the tick thread. Async jobs are charged only for submission and waits.</summary>
[CVarDefs]
internal sealed partial class ServerTickTiming
{
    public static readonly CVarDef<bool> LogEnabled = CVarDef.Create(
        "log.tick_timing", true, CVar.SERVERONLY, "Log summaries and slow ticks for server simulation and PVS.");

    private static readonly long SummaryInterval = 60 * Stopwatch.Frequency;
    private static readonly long SlowInterval = 15 * Stopwatch.Frequency;
    private static readonly double MillisecondsPerTimestamp = 1000d / Stopwatch.Frequency;

    internal enum Phase
    {
        Setup,
        Simulation,
        PostSimulation,
        PvsSessions,
        PvsWaitPrevious,
        PvsPrepare,
        PvsVisibilityAndAcks,
        PvsSerialize,
        PvsSend,
        PvsCleanup,
        PvsTickMaintenance,
        PvsDispatch,
    }

    private static readonly string[] PhaseNames =
    [
        "setup", "simulation", "post_simulation", "pvs_sessions", "pvs_wait_previous", "pvs_prepare",
        "pvs_visibility_acks", "pvs_serialize", "pvs_send", "pvs_cleanup", "pvs_tick_maintenance", "pvs_dispatch"
    ];

    private readonly long[] _sample = new long[PhaseNames.Length];
    private readonly long[] _sums = new long[PhaseNames.Length];
    private readonly long[] _maxima = new long[PhaseNames.Length];
    private readonly long[] _worstPhases = new long[PhaseNames.Length];
    private ISawmill _log = default!;
    private bool _reset;
    private long _start;
    private long _phaseStart;
    private long _pvsStart;
    private long _summaryStart;
    private long _slowWindowStart;
    private int _players;
    private bool _asyncPvs;
    private int _samples;
    private int _overBudget;
    private int _minPlayers;
    private int _maxPlayers;
    private int _asyncTicks;
    private long _total;
    private long _max;
    private int _slowCount;
    private long _worst;
    private long _worstEnded;
    private GameTick _worstTick;
    private int _worstPlayers;
    private bool _worstAsync;
    private double _worstBudgetMs;

    public bool Enabled { get; private set; }
    public bool Collecting { get; private set; }
    private bool _basicEnabled;
    public int CaptureGeneration { get; private set; }
    private Robust.Shared.GameObjects.IComponentFactory? _componentFactory;

    public void Initialize(ISawmill log, Robust.Shared.GameObjects.IComponentFactory? factory = null)
    { _log = log; _componentFactory = factory; }

    public void SetEnabled(bool enabled)
    {
        _basicEnabled = enabled;
        CaptureGeneration++;
        InvalidatePerformanceReports();
        Enabled = enabled || DetailLevel > 0;
        _reset = true;
        if (!Enabled)
            Collecting = false;
    }

    public void BeginTick() => BeginTick(Stopwatch.GetTimestamp());

    internal void BeginTick(long now)
    {
        if (_reset)
        {
            ResetSummary(now);
            _slowWindowStart = now;
            _slowCount = 0;
            _worst = 0;
            _reset = false;
            ResetDiagnostics();
        }

        Array.Clear(_sample);
        _start = _phaseStart = _pvsStart = now;
        _players = 0;
        _asyncPvs = false;
        Collecting = true;
        BeginDiagnostics();
    }

    public void Mark(Phase phase) => Mark(phase, Stopwatch.GetTimestamp());

    internal void Mark(Phase phase, long now)
    {
        _sample[(int) phase] += now - _phaseStart;
        _phaseStart = now;
        if (phase == Phase.PostSimulation)
            _pvsStart = now;
    }

    public void SetPvsPlayers(int count) => _players = count;
    public void SetAsyncPvs(bool enabled) => _asyncPvs = enabled;
    public void RecordPvsMaintenance(long duration) => _sample[(int) Phase.PvsTickMaintenance] += duration;
    public void StopTick() => Collecting = false;

    public void CompleteTick(GameTick tick, TimeSpan budget) => CompleteTick(tick, budget, Stopwatch.GetTimestamp());

    internal void CompleteTick(GameTick tick, TimeSpan budget, long now)
    {
        var elapsed = now - _start;
        _performanceEndTick = tick.Value;
        var budgetMs = budget.TotalMilliseconds;
        CompleteDiagnostics();
        RecordPerformanceTick(elapsed);
        _sample[(int) Phase.PvsDispatch] = _phaseStart - _pvsStart;
        _samples++;
        _total += elapsed;
        _max = Math.Max(_max, elapsed);
        _minPlayers = Math.Min(_minPlayers, _players);
        _maxPlayers = Math.Max(_maxPlayers, _players);
        if (_asyncPvs)
            _asyncTicks++;

        for (var i = 0; i < _sample.Length; i++)
        {
            _sums[i] += _sample[i];
            _maxima[i] = Math.Max(_maxima[i], _sample[i]);
        }

        if (elapsed * MillisecondsPerTimestamp > budgetMs)
        {
            _overBudget++;
            _slowCount++;
            if (elapsed > _worst)
            {
                _worst = elapsed;
                _worstEnded = now;
                _worstTick = tick;
                _worstPlayers = _players;
                _worstAsync = _asyncPvs;
                _worstBudgetMs = budgetMs;
                _sample.CopyTo(_worstPhases, 0);
                CaptureSlowDiagnostics();
            }
        }

        if (now - _slowWindowStart >= (DetailLevel > 0 ? _detailSlowInterval : SlowInterval))
        {
            if (_slowCount > 0 && _log.IsLogLevelEnabled(LogLevel.Warning))
                LogSlowTick(now);
            _slowWindowStart = now;
            _slowCount = 0;
            _worst = 0;
            LogSlowPvs(budgetMs);
            FlushPerformanceWarnings();
        }

        if (now - _summaryStart < (DetailLevel > 0 ? _detailSummaryInterval : SummaryInterval))
            return;

        if (_log.IsLogLevelEnabled(LogLevel.Info))
        {
            LogSummary(now, budgetMs);
            LogPerformanceDetails(now);
        }
        ResetSummary(now);
        ResetDiagnosticSummary();
    }

    private void ResetSummary(long now)
    {
        _summaryStart = now;
        _samples = _overBudget = _maxPlayers = _asyncTicks = 0;
        _minPlayers = int.MaxValue;
        _total = _max = 0;
        Array.Clear(_sums);
        Array.Clear(_maxima);
    }

    private void LogSlowTick(long now)
    {
        var msg = new StringBuilder(512);
        msg.Append(CultureInfo.InvariantCulture,
            $"Slow server tick: tick={_worstTick.Value} players={_worstPlayers} async_pvs={_worstAsync} over_budget_ticks={_slowCount} age_ms={(now - _worstEnded) * MillisecondsPerTimestamp:F2} elapsed_ms={_worst * MillisecondsPerTimestamp:F2} budget_ms={_worstBudgetMs:F2}");
        for (var i = 0; i < PhaseNames.Length; i++)
            msg.Append(CultureInfo.InvariantCulture, $" {PhaseNames[i]}_ms={_worstPhases[i] * MillisecondsPerTimestamp:F2}");
        AppendSlowDiagnostics(msg);
        AppendSlowPerformanceDetails(msg);
        if (DetailLevel > 0) QueuePerformanceWarning(msg.ToString());
        else _log.Warning(msg.ToString());
    }

    private void LogSummary(long now, double budgetMs)
    {
        var seconds = (now - _summaryStart) / (double) Stopwatch.Frequency;
        var msg = new StringBuilder(768);
        msg.Append(CultureInfo.InvariantCulture,
            $"Server tick timing summary: window_s={seconds:F1} ticks={_samples} tps={_samples / seconds:F2} players_min={_minPlayers} players_max={_maxPlayers} async_pvs_ticks={_asyncTicks} over_budget_ticks={_overBudget} budget_ms={budgetMs:F2} tick_avg_ms={_total * MillisecondsPerTimestamp / _samples:F2} tick_max_ms={_max * MillisecondsPerTimestamp:F2}");
        for (var i = 0; i < PhaseNames.Length; i++)
        {
            var name = PhaseNames[i];
            msg.Append(CultureInfo.InvariantCulture,
                $" {name}_avg_ms={_sums[i] * MillisecondsPerTimestamp / _samples:F2} {name}_max_ms={_maxima[i] * MillisecondsPerTimestamp:F2}");
        }
        AppendDiagnosticSummary(msg);
        if (DetailLevel > 0) _performanceSummaryLine = msg.ToString();
        else _log.Info(msg.ToString());
    }
}
