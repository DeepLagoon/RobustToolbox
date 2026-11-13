using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Prometheus;
using Robust.Server.GameStates;
using Robust.Server.Player;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
#if EXCEPTION_TOLERANCE
using Robust.Shared.Exceptions;
#endif
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Replays;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Server.GameObjects
{
    /// <summary>
    /// Manager for entities -- controls things like template loading and instantiation
    /// </summary>
    [UsedImplicitly] // DI Container
    public sealed partial class ServerEntityManager : EntityManager, IServerEntityManager
    {
        private static readonly Gauge EntitiesCount = Metrics.CreateGauge(
            "robust_entities_count",
            "Amount of alive entities.");

        [Dependency] private IReplayRecordingManager _replay = default!;
        [Dependency] private IServerNetManager _networkManager = default!;
        [Dependency] private IGameTiming _gameTiming = default!;
        [Dependency] private IPlayerManager _playerManager = default!;
        [Dependency] private IConfigurationManager _configurationManager = default!;
#if EXCEPTION_TOLERANCE
        [Dependency] private IRuntimeLog _runtimeLog = default!;
#endif

        private ISawmill _netEntSawmill = default!;
        private PvsSystem _pvs = default!;
        private Histogram? _tickUpdateHistogram;
        private Histogram.Child? _entityNetHistogram;

        public override void Initialize()
        {
            _netEntSawmill = LogManager.GetSawmill("net.ent");

            SetupNetworking();
            ReceivedSystemMessage += (_, systemMsg) => EventBus.RaiseEvent(EventSource.Network, systemMsg);

            base.Initialize();
        }

        public override void Startup()
        {
            base.Startup();
            _pvs = System<PvsSystem>();
        }

        internal override EntityUid CreateEntity(string? prototypeName, out MetaDataComponent metadata, IEntityLoadContext? context = null)
        {
            if (prototypeName == null)
                return base.CreateEntity(prototypeName, out metadata, context);

            if (!PrototypeManager.TryIndex<EntityPrototype>(prototypeName, out var prototype))
                throw new EntityCreationException($"Attempted to spawn an entity with an invalid prototype: {prototypeName}");

            var entity = base.CreateEntity(prototype, out metadata, context);

            // At this point in time, all data configure on the entity *should* be purely from the prototype.
            // As such, we can reset the modified ticks to Zero,
            // which indicates "not different from client's own deserialization".
            // So the initial data for the component or even the creation doesn't have to be sent over the wire.
            ClearTicks(entity, prototype);
            return entity;
        }

        /// <inheritdoc />
        public override void RaiseSharedEvent<T>(T message, EntityUid? user = null)
        {
            if (user != null)
            {
                var filter = Filter.Broadcast().RemoveWhereAttachedEntity(e => e == user.Value);
                foreach (var session in filter.Recipients)
                {
                    EntityNetManager.SendSystemNetworkMessage(message, session.Channel);
                }
            }
            else
            {
                EntityNetManager.SendSystemNetworkMessage(message);
            }
        }

        /// <inheritdoc />
        public override void RaiseSharedEvent<T>(T message, ICommonSession? user = null)
        {
            if (user != null)
            {
                var filter = Filter.Broadcast().RemovePlayer(user);
                foreach (var session in filter.Recipients)
                {
                    EntityNetManager.SendSystemNetworkMessage(message, session.Channel);
                }
            }
            else
            {
                EntityNetManager.SendSystemNetworkMessage(message);
            }
        }

        private void ClearTicks(EntityUid entity, EntityPrototype prototype)
        {
            foreach (var (netId, component) in GetNetComponents(entity))
            {
                // Make sure to ONLY get components that are defined in the prototype.
                // Others could be instantiated directly by AddComponent (e.g. ContainerManager).
                // And those aren't guaranteed to exist on the client, so don't clear them.
                var compName = ComponentFactory.GetComponentName(netId);
                if (prototype.Components.ContainsKey(compName))
                    component.ClearTicks();
            }
        }

        internal override void SetLifeStage(MetaDataComponent meta, EntityLifeStage stage)
        {
            base.SetLifeStage(meta, stage);
            _pvs.SyncMetadata(meta);
        }

        #region IEntityNetworkManager impl

        public override IEntityNetworkManager EntityNetManager => this;

        /// <inheritdoc />
        public event EventHandler<object>? ReceivedSystemMessage;

        private readonly PriorityQueue<MsgEntity> _queue = new(new MessageSequenceComparer());

        private readonly Dictionary<ICommonSession, uint> _lastProcessedSequencesCmd =
            new();

        private static readonly TimeSpan LateMessageLogInterval = TimeSpan.FromSeconds(1);
        private bool _logLateMsgs;
        private TimeSpan _nextLateMessageLog;
        private int _lateMessageCount;
        private uint _maxLateMessageTicks;

        /// <inheritdoc />
        public void SetupNetworking()
        {
            _networkManager.RegisterNetMessage<MsgEntity>(HandleEntityNetworkMessage);

            _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;

            _configurationManager.OnValueChanged(CVars.NetLogLateMsg, enabled =>
            {
                _logLateMsgs = enabled;
                _lateMessageCount = 0;
                _maxLateMessageTicks = 0;
            }, true);
        }

        /// <inheritdoc />
        public override void TickUpdate(float frameTime, bool noPredictions, Histogram? histogram)
        {
            UpdateTickHistogram(histogram);

            using (_entityNetHistogram?.NewTimer())
            {
                while (_queue.Count != 0 && _queue.Peek().SourceTick <= _gameTiming.CurTick)
                {
                    DispatchEntityNetworkMessage(_queue.Take());
                }
            }

            if (_lateMessageCount > 0 && _gameTiming.RealTime >= _nextLateMessageLog)
            {
                _netEntSawmill.Warning("Received {Count} late entity messages (maximum delay: {Ticks} ticks).",
                    _lateMessageCount, _maxLateMessageTicks);
                _lateMessageCount = 0;
                _maxLateMessageTicks = 0;
                _nextLateMessageLog = _gameTiming.RealTime + LateMessageLogInterval;
            }

            base.TickUpdate(frameTime, noPredictions, histogram);

            EntitiesCount.Set(Entities.Count);
        }

        private void UpdateTickHistogram(Histogram? histogram)
        {
            if (ReferenceEquals(_tickUpdateHistogram, histogram))
                return;

            _tickUpdateHistogram = histogram;
            _entityNetHistogram = histogram?.WithLabels("EntityNet");
        }

        public uint GetLastMessageSequence(ICommonSession? session)
        {
            return session == null ? default : _lastProcessedSequencesCmd.GetValueOrDefault(session);
        }

        /// <inheritdoc />
        public void SendSystemNetworkMessage(EntityEventArgs message, bool recordReplay = true)
        {
            var newMsg = new MsgEntity();
            newMsg.Type = EntityMessageType.SystemMessage;
            newMsg.SystemMessage = message;
            newMsg.SourceTick = _gameTiming.CurTick;

            if (recordReplay)
                _replay.RecordServerMessage(message);

            _networkManager.ServerSendToAll(newMsg);
        }

        /// <inheritdoc />
        public void SendSystemNetworkMessage(EntityEventArgs message, INetChannel targetConnection)
        {
            var newMsg = new MsgEntity();
            newMsg.Type = EntityMessageType.SystemMessage;
            newMsg.SystemMessage = message;
            newMsg.SourceTick = _gameTiming.CurTick;

            _networkManager.ServerSendMessage(newMsg, targetConnection);
        }

        private void HandleEntityNetworkMessage(MsgEntity message)
        {
            // The transport is connected before the player session is ready to send entity messages.
            if (!TryGetEntityMessageSession(message.MsgChannel, out _, out _))
                return;

            if (_logLateMsgs)
            {
                var currentTick = _gameTiming.CurTick;
                if (message.SourceTick < currentTick)
                {
                    _lateMessageCount++;
                    _maxLateMessageTicks = Math.Max(_maxLateMessageTicks, currentTick.Value - message.SourceTick.Value);
                }
            }

            _queue.Add(message);
        }

        private void DispatchEntityNetworkMessage(MsgEntity message)
        {
            // A queued message can outlive the session or channel that sent it.
            if (!TryGetEntityMessageSession(message.MsgChannel, out var player, out var lastSequence))
                return;

            if (message.Sequence > lastSequence)
                _lastProcessedSequencesCmd[player] = message.Sequence;

#if EXCEPTION_TOLERANCE
            try
#endif
            {
                switch (message.Type)
                {
                    case EntityMessageType.SystemMessage:
                        var msg = message.SystemMessage;
                        var sessionType = typeof(EntitySessionMessage<>).MakeGenericType(msg.GetType());
                        var sessionMsg =
                            Activator.CreateInstance(sessionType, new EntitySessionEventArgs(player), msg)!;
                        ReceivedSystemMessage?.Invoke(this, msg);
                        ReceivedSystemMessage?.Invoke(this, sessionMsg);
                        return;
                }
            }
#if EXCEPTION_TOLERANCE
            catch (Exception e)
            {
                _runtimeLog.LogException(e, $"{nameof(DispatchEntityNetworkMessage)}({message.Type})");
            }
#endif
        }

        private bool TryGetEntityMessageSession(
            INetChannel channel,
            [NotNullWhen(true)] out ICommonSession? player,
            out uint lastSequence)
        {
            player = null;
            lastSequence = 0;
            return channel.IsConnected
                && _playerManager.TryGetSessionByChannel(channel, out player)
                && ReferenceEquals(player.Channel, channel)
                && _lastProcessedSequencesCmd.TryGetValue(player, out lastSequence);
        }

        private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
        {
            switch (args.NewStatus)
            {
                case SessionStatus.Connected:
                    _lastProcessedSequencesCmd.Add(args.Session, 0);
                    break;

                case SessionStatus.Disconnected:
                    _lastProcessedSequencesCmd.Remove(args.Session);
                    break;
            }
        }

        internal sealed class MessageSequenceComparer : IComparer<MsgEntity>
        {
            public int Compare(MsgEntity? x, MsgEntity? y)
            {
                DebugTools.AssertNotNull(x);
                DebugTools.AssertNotNull(y);

                var cmp = y!.SourceTick.CompareTo(x!.SourceTick);
                if (cmp != 0)
                {
                    return cmp;
                }

                return y.Sequence.CompareTo(x.Sequence);
            }
        }

        #endregion
    }
}
