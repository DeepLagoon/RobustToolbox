using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Server.GameStates;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Timing;

namespace Robust.UnitTesting.Server.GameStates;

public sealed class InitialStateBacklogTest : RobustIntegrationTest
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task CreationBurstAfterLobbyAckOnlyPacesBulkSnapshots(bool bulk)
    {
        await using var pair = await StartConnectedPair(new() { Pool = false }, new() { Pool = false });
        var (client, server) = pair;
        await RunTicksSync(server, client, 5);
        EntityUid map = default;
        await server.WaitPost(() =>
        {
            server.CfgMan.SetCVar(CVars.NetPVS, false);
            map = server.System<SharedMapSystem>().CreateMap();
            server.PlayerMan.JoinGame(server.PlayerMan.Sessions.Single());
        });
        await RunTicksSync(server, client, 10);
        await server.WaitRunTicks(1); // Process the last small lobby ACK before pausing the client.

        GameTick lobbyAck = default;
        await server.WaitAssertion(() =>
        {
            lobbyAck = server.System<PvsSystem>().PlayerData[server.PlayerMan.Sessions.Single()].LastReceivedAck;
            Assert.That(lobbyAck, Is.GreaterThan(GameTick.Zero));
        });
        var received = new List<(Robust.Shared.GameStates.GameState State, int Bytes)>();
        await client.WaitPost(() =>
        {
            var field = client.NetMan.GetType().GetField("_callbacks", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var callbacks = (Dictionary<Type, ProcessMessage>) field.GetValue(client.NetMan)!;
            var original = callbacks[typeof(MsgState)];
            callbacks[typeof(MsgState)] = message =>
            {
                var state = (MsgState) message;
                if (state.MsgSize > MsgState.ReliableThreshold)
                    received.Add((state.State, state.MsgSize));
                original(message);
            };
        });
        EntityUid added = default;
        await server.WaitPost(() =>
        {
            var random = new Random(20261104);
            for (var i = 0; i < (bulk ? 2000 : 1); i++)
            {
                added = server.EntMan.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero));
                var bytes = new byte[bulk ? 128 : 2048];
                random.NextBytes(bytes);
                server.System<MetaDataSystem>().SetEntityName(added, Convert.ToHexString(bytes));
            }
        });
        // Round/map creation can be a large delta even after the lobby was acknowledged.
        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
            Assert.That(server.System<PvsSystem>().PlayerData[server.PlayerMan.Sessions.Single()].LastReceivedAck,
                Is.EqualTo(lobbyAck)));
        await client.WaitRunTicks(1);
        await client.WaitAssertion(() =>
        {
            Assert.That(received[0].State.FromSequence, Is.GreaterThan(GameTick.Zero));
            if (bulk)
                Assert.That(received[0].Bytes, Is.GreaterThan(64 * 1024));
            else
                Assert.That(received[0].Bytes, Is.LessThan(64 * 1024));
            Assert.That(received.Count, Is.EqualTo(bulk ? 1 : 10),
                "Acknowledging a small lobby state must not allow a heavy creation snapshot to flood the reliable queue");
        });
        await RunTicksSync(server, client, 10);
        await client.WaitAssertion(() =>
            Assert.That(client.EntMan.TryGetEntity(server.EntMan.GetNetEntity(added), out _), Is.True));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DelayedInitialAckDoesNotQueueAFullWorldEveryTick(bool waitForRetry)
    {
        await using var pair = await StartConnectedPair(new() { Pool = false }, new() { Pool = false });
        var (client, server) = pair;
        await RunTicksSync(server, client, 5);
        var received = new List<Robust.Shared.GameStates.GameState>();
        await client.WaitPost(() =>
        {
            var field = client.NetMan.GetType().GetField("_callbacks", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var callbacks = (Dictionary<Type, ProcessMessage>) field.GetValue(client.NetMan)!;
            var original = callbacks[typeof(MsgState)];
            callbacks[typeof(MsgState)] = message =>
            {
                received.Add(((MsgState) message).State);
                if (waitForRetry && received.Count == 1)
                    return;
                original(message);
            };
        });

        EntityUid map = default;
        EntityUid deleted = default;
        await server.WaitPost(() =>
        {
            server.CfgMan.SetCVar(CVars.NetPVS, false);
            map = server.System<SharedMapSystem>().CreateMap();
            var random = new Random(20261103);
            for (var i = 0; i < 150; i++)
            {
                var uid = server.EntMan.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero));
                if (i == 0)
                    deleted = uid;
                var bytes = new byte[64];
                random.NextBytes(bytes);
                server.System<MetaDataSystem>().SetEntityName(uid, Convert.ToHexString(bytes));
            }
            server.PlayerMan.JoinGame(server.PlayerMan.Sessions.Single());
        });

        // The client's network loop is paused, as during initial scene loading.
        var ticks = 10;
        if (waitForRetry)
            await server.WaitPost(() => ticks += 5 * server.ResolveDependency<IGameTiming>().TickRate);
        await server.WaitRunTicks(ticks);
        EntityUid added = default;
        NetEntity deletedNet = default;
        await server.WaitAssertion(() =>
        {
            var session = server.PlayerMan.Sessions.Single();
            Assert.That(server.System<PvsSystem>().PlayerData[session].LastReceivedAck, Is.EqualTo(GameTick.Zero),
                "Queueing reliable transport must never manufacture an acknowledgement");
            added = server.EntMan.SpawnEntity(null, new EntityCoordinates(map, Vector2.One));
            deletedNet = server.EntMan.GetNetEntity(deleted);
            server.EntMan.DeleteEntity(deleted);
        });
        await client.WaitRunTicks(1);
        await client.WaitAssertion(() =>
        {
            var full = received.Where(state => state.FromSequence == GameTick.Zero).ToArray();
            Assert.That(full.Length, Is.EqualTo(waitForRetry ? 2 : 1),
                "A slow initial receiver must not accumulate a full-world replay on every server tick");
            Assert.That(full[0].PayloadSize, Is.GreaterThan(MsgState.ReliableThreshold));
        });
        await RunTicksSync(server, client, 10);
        await server.WaitAssertion(() =>
            Assert.That(server.System<PvsSystem>().PlayerData[server.PlayerMan.Sessions.Single()].LastReceivedAck,
                Is.GreaterThan(GameTick.Zero)));
        await client.WaitAssertion(() =>
        {
            Assert.That(client.EntMan.TryGetEntity(server.EntMan.GetNetEntity(added), out _), Is.True,
                "An entity created while the initial state was in flight must arrive after the real acknowledgement");
            Assert.That(client.EntMan.TryGetEntity(deletedNet, out var uid) && client.EntMan.EntityExists(uid), Is.False,
                "Deletion history must survive the ticks where initial-state serialization is deferred");
        });
    }
}
