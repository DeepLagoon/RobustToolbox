using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Client.GameStates;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Robust.UnitTesting.Server.GameStates;

public sealed class PvsReliableAckTest : RobustIntegrationTest
{
#if DEBUG
    [TestCase(1, 3)]
    [TestCase(40, 3)]
    [TestCase(40, 70)]
    public async Task UnreceivedCreationStateMustNotAcknowledgeNewEntities(int count, int outageTicks)
    {
        await using var pair = await StartConnectedPair();
        var (client, server) = pair;
        var states = (ClientGameStateManager)client.ResolveDependency<IClientGameStateManager>();
        await RunTicksSync(server, client, 10);
        EntityUid map = default;
        EntityUid player = default;
        EntityUid[] targets = [];
        await server.WaitPost(() =>
        {
            server.CfgMan.SetCVar(CVars.NetPVS, true);
            map = server.System<SharedMapSystem>().CreateMap();
            player = server.EntMan.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero));
            var session = server.PlayerMan.Sessions.Single();
            server.PlayerMan.SetAttachedEntity(session, player);
            server.PlayerMan.JoinGame(session);
        });
        await RunTicksSync(server, client, 10);

        // A newly visible room can produce a large, reliable creation state. Fault injection models a
        // state the client never accepts; transport send completion must not count as an entity ack.
        await client.WaitPost(() => states.DropStates = true);
        await server.WaitPost(() =>
        {
            var random = new System.Random(20610789);
            targets = Enumerable.Range(0, count).Select(_ =>
            {
                var uid = server.EntMan.SpawnEntity(null, new EntityCoordinates(map, Vector2.One));
                var bytes = new byte[96];
                random.NextBytes(bytes);
                server.System<MetaDataSystem>().SetEntityName(uid, System.Convert.ToHexString(bytes));
                return uid;
            }).ToArray();
        });
        await RunTicksSync(server, client, outageTicks);
        await client.WaitPost(() => states.DropStates = false);
        await server.WaitPost(() =>
        {
            foreach (var uid in targets)
                server.System<SharedTransformSystem>().SetCoordinates(uid, new EntityCoordinates(map, new Vector2(1.1f, 1)));
        });
        await RunTicksSync(server, client, 20);
        await client.WaitAssertion(() =>
        {
            foreach (var uid in targets)
                Assert.That(client.EntMan.TryGetEntity(server.EntMan.GetNetEntity(uid), out _), Is.True);
        });
    }
#endif
}
