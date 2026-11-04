using Moq;
using NUnit.Framework;
using Robust.Server.GameObjects;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using RobustIntegrationTest = Robust.UnitTesting.RobustIntegrationTest;

namespace Robust.Server.IntegrationTests.GameObjects
{
    internal sealed class ServerEntityNetworkManagerTest : RobustIntegrationTest
    {
        [Test]
        public async Task EntityMessagesRequireReadySessionOnCurrentChannel()
        {
            var server = StartServer();
            await server.WaitIdleAsync();

            var players = server.PlayerMan;
            var net = (IClientNetManager) server.NetMan;
            var entityNet = server.ResolveDependency<IServerEntityNetworkManager>();
            var userId = new NetUserId(Guid.NewGuid());
            var firstChannel = CreateChannel();
            var nextChannel = CreateChannel();
            var received = new List<object>();
            entityNet.ReceivedSystemMessage += (_, message) => received.Add(message);

            await server.WaitPost(() =>
            {
                Send(firstChannel, 100);
                var session = players.CreateAndAddSession(firstChannel);
                Send(firstChannel, 101);
                Send(firstChannel, 0);
                players.SetStatus(session, SessionStatus.Connected);
            });
            await server.WaitRunTicks(1);

            await server.WaitPost(() =>
            {
                Assert.That(received, Is.Empty);
                Send(firstChannel, 102);
                players.GetSessionById(userId).Channel = nextChannel;
                Send(firstChannel, 103);
                Send(nextChannel, 0);
                Send(nextChannel, 5);
                Send(nextChannel, 4);
            });
            await server.WaitRunTicks(1);

            await server.WaitPost(() =>
            {
                Assert.That(received.OfType<TestEntityEvent>().Count(), Is.EqualTo(3));
                Assert.That(entityNet.GetLastMessageSequence(players.GetSessionById(userId)), Is.EqualTo(5));
            });

            INetChannel CreateChannel()
            {
                var channel = new Mock<INetChannel>();
                channel.SetupGet(c => c.UserId).Returns(userId);
                channel.SetupGet(c => c.UserName).Returns("EntityMessageTest");
                channel.SetupGet(c => c.IsConnected).Returns(true);
                return channel.Object;
            }

            void Send(INetChannel channel, uint sequence)
            {
                net.DispatchLocalNetMessage(new MsgEntity
                {
                    MsgChannel = channel,
                    Type = EntityMessageType.SystemMessage,
                    SystemMessage = new TestEntityEvent(),
                    Sequence = sequence,
                });
            }
        }

        private sealed class TestEntityEvent : EntityEventArgs;

        [Test]
        public void TestMessageSort()
        {
            var tickA = new GameTick(5);
            var tickB = new GameTick(3);
            var channel = new Mock<INetChannel>().Object;
            var msgA = new MsgEntity() {MsgChannel = channel, Type = EntityMessageType.SystemMessage, SourceTick = tickA, Sequence = 10};
            var msgB = new MsgEntity() {MsgChannel = channel, Type = EntityMessageType.SystemMessage, SourceTick = tickA, Sequence = 13};
            var msgC = new MsgEntity() {MsgChannel = channel, Type = EntityMessageType.SystemMessage, SourceTick = tickA, Sequence = 12};
            var msgD = new MsgEntity() {MsgChannel = channel, Type = EntityMessageType.SystemMessage, SourceTick = tickA, Sequence = 14};
            var msgE = new MsgEntity() {MsgChannel = channel, Type = EntityMessageType.SystemMessage, SourceTick = tickB, Sequence = 7};
            var msgF = new MsgEntity() {MsgChannel = channel, Type = EntityMessageType.SystemMessage, SourceTick = tickB, Sequence = 4};

            var pq = new PriorityQueue<MsgEntity>(new ServerEntityManager.MessageSequenceComparer())
            {
                msgA,
                msgB,
                msgC,
                msgD,
                msgE,
                msgF
            };


            Assert.That(pq.Take(), Is.EqualTo(msgF));
            Assert.That(pq.Take(), Is.EqualTo(msgE));
            Assert.That(pq.Take(), Is.EqualTo(msgA));
            Assert.That(pq.Take(), Is.EqualTo(msgC));
            Assert.That(pq.Take(), Is.EqualTo(msgB));
            Assert.That(pq.Take(), Is.EqualTo(msgD));
        }
    }
}
