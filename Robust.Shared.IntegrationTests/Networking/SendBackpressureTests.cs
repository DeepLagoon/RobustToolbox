using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using Lidgren.Network;
using NUnit.Framework;

namespace Robust.UnitTesting.Shared.Networking;

[TestFixture]
public sealed class SendBackpressureTests
{
    private static readonly IPEndPoint Target = new(IPAddress.Loopback, 1213);

    [TestCase(50)]
    [TestCase(100)]
    [TestCase(150)]
    [TestCase(250)]
    public void ReliableProducerCoalescingBoundsBacklogAtHighRoundTripTimes(int milliseconds)
    {
        var unpaced = RunProducer(milliseconds, false);
        var paced = RunProducer(milliseconds, true);
        TestContext.Out.WriteLine($"RTT={milliseconds}ms; unpaced max queued fragments={unpaced}; paced={paced}");
        Assert.That(paced, Is.LessThanOrEqualTo(30), "At most one newly prepared snapshot may exceed the free window");
        if (milliseconds >= 100)
            Assert.That(unpaced, Is.GreaterThan(1000), "The workload must reproduce a growing reliable backlog");
    }

    private static int RunProducer(int milliseconds, bool pace)
    {
        var peer = new NetPeer(new NetPeerConfiguration("rtt-state-producer")
        {
            EnableSendBackpressure = true,
            ReliableSendWindowBytes = 131072,
            MaximumTransmissionUnit = 700,
            // No socket or network thread: drive real sender windows and timed ACKs below.
            // Suppress actual datagram transmission while retaining sender bookkeeping.
            SimulatedLoss = 1,
        });
        typeof(NetPeer).GetField("m_networkThread", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(peer, Thread.CurrentThread);
        var connection = new NetConnection(peer, Target);
        var rtt = milliseconds / 1000.0;
        typeof(NetConnection).GetField("m_averageRoundtripTime", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(connection, rtt);
        var channel = new NetReliableSenderChannel(connection, NetUtility.GetWindowSize(NetDeliveryMethod.ReliableUnordered));
        connection.m_sendChannels[(int) NetDeliveryMethod.ReliableUnordered - 1] = channel;
        var windowStart = typeof(NetReliableSenderChannel).GetField("m_windowStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var payload = new byte[680];
        var nextFrame = 0;
        var maximumQueued = 0;
        for (var elapsed = 0; elapsed < 20000; elapsed++)
        {
            var now = 1 + elapsed / 1000.0;
            var start = (int) windowStart.GetValue(channel)!;
            for (var i = 0; i < channel.WindowSize; i++)
            {
                var sequence = (start + i) % 1024;
                var stored = channel.m_storedMessages[sequence % channel.WindowSize];
                if (stored.Message == null || stored.SequenceNumber != sequence || now < stored.LastSent + rtt)
                    break;
                channel.ReceiveAcknowledge(now, sequence);
            }
            if (elapsed * 30 >= nextFrame * 1000)
            {
                nextFrame++;
                // One roughly 20 KiB compressed world update at 30 server ticks/sec.
                // A skipped producer tick is replaced by a fresh snapshot after capacity returns.
                if (!pace || connection.CanSendImmediately(NetDeliveryMethod.ReliableUnordered, 0))
                {
                    for (var fragment = 0; fragment < 30; fragment++)
                    {
                        var message = peer.CreateMessage();
                        message.Write(payload);
                        message.m_messageType = NetMessageType.UserReliableUnordered;
                        message.m_recyclingCount = 1;
                        channel.Enqueue(message);
                    }
                }
            }
            channel.SendQueuedMessages(now);
            maximumQueued = Math.Max(maximumQueued, channel.QueuedSendsCount);
        }
        channel.Reset();
        return maximumQueued;
    }

    [Test]
    public void ChannelsShareWindowAndOnlyAcknowledgementOrResetReleasesReservation()
    {
        var peer = new NetPeer(new NetPeerConfiguration("byte-window-regression")
        {
            EnableSendBackpressure = true, ReliableSendWindowBytes = 40,
        });
        // Drive sender channels synchronously, without binding a socket or starting a thread.
        typeof(NetPeer).GetField("m_networkThread", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(peer, Thread.CurrentThread);
        var connection = new NetConnection(peer, Target);
        var first = new NetReliableSenderChannel(connection, 64);
        var second = new NetReliableSenderChannel(connection, 64);
        NetOutgoingMessage Message()
        {
            var message = peer.CreateMessage();
            message.Write(new byte[20]);
            message.m_messageType = NetMessageType.UserReliableOrdered1;
            message.m_recyclingCount = 1;
            return message;
        }

        first.Enqueue(Message());
        second.Enqueue(Message());
        first.SendQueuedMessages(1);
        second.SendQueuedMessages(1);
        Assert.That(connection.ReliableByteWindow.Bytes, Is.EqualTo(25));
        Assert.That(second.QueuedSendsCount, Is.EqualTo(1));
        first.ReceiveAcknowledge(1.001, 0);
        second.SendQueuedMessages(1.002);
        Assert.That(second.QueuedSendsCount, Is.Zero);
        Assert.That(connection.ReliableByteWindow.Bytes, Is.EqualTo(25));
        second.SendQueuedMessages(2); // Retransmission must not reserve the same bytes twice.
        Assert.That(connection.ReliableByteWindow.Bytes, Is.EqualTo(25));
        second.Reset();
        Assert.That(connection.ReliableByteWindow.Bytes, Is.Zero);
        first.Reset();
        Assert.That(connection.ReliableByteWindow.Bytes, Is.Zero);
    }

    [Test]
    public void UnreliableQueueRemainsBoundedWhenAcknowledgementsAreSuppressed()
    {
        var peer = new NetPeer(new NetPeerConfiguration("unreliable-bound-regression")
        {
            EnableSendBackpressure = true, SuppressUnreliableUnorderedAcks = true,
        });
        var channel = new NetUnreliableSenderChannel(new NetConnection(peer, Target), 128, NetDeliveryMethod.Unreliable);
        for (var i = 0; i < 128; i++)
            Assert.That(channel.Enqueue(peer.CreateMessage()), Is.EqualTo(NetSendResult.Sent));
        Assert.That(channel.Enqueue(peer.CreateMessage()), Is.EqualTo(NetSendResult.Dropped));
        Assert.That(channel.QueuedSendsCount, Is.EqualTo(128));
    }

    [Test]
    public void CongestionReducesOncePerEpisodeAndAcknowledgementsRecoverWithinCap()
    {
        var window = new NetReliableByteWindow();
        Assert.That(window.CanSend(1000, 131072), Is.True);
        window.Congestion(1, .1);
        Assert.That(window.Limit, Is.EqualTo(65536));
        window.Congestion(1.01, .1);
        Assert.That(window.Limit, Is.EqualTo(65536));
        for (var i = 0; i < 10; i++) window.Congestion(2 + i, .1);
        Assert.That(window.Limit, Is.EqualTo(8192));
        for (var i = 0; i < 20000; i++)
        {
            window.Reserve(1024);
            window.Acknowledge(1024);
        }
        Assert.That(window.Limit, Is.EqualTo(131072));
        Assert.That(window.Bytes, Is.Zero);
    }

    [Test]
    public void ByteWindowIsSharedAndOversizeMessageCannotDeadlockEmptyWindow()
    {
        var window = new NetReliableByteWindow();
        window.Reserve(60);
        Assert.That(window.CanSend(41, 100), Is.False);
        Assert.That(window.CanSend(40, 100), Is.True);
        window.Reserve(40);
        Assert.That(window.CanSend(1, 100), Is.False);
        window.Release(60);
        Assert.That(window.CanSend(60, 100), Is.True);
        window.Release(40);
        Assert.That(window.CanSend(200, 100), Is.True);
        window.Reserve(200);
        Assert.That(window.CanSend(1, 100), Is.False);
        Assert.That(window.CanSend(1, 0), Is.True);
        window.Release(200);
        Assert.That(window.Bytes, Is.Zero);
    }

    [Test]
    public void RetryOwnsSnapshotAndCountsOnlySuccessfulSend()
    {
        var blocked = true;
        var sent = 0;
        var recycled = 0;
        var received = new List<byte>();
        var queue = new NetPendingSendQueue((data, _, endpoint) =>
        {
            if (blocked) return NetSocketSendResult.WouldBlock;
            received.Add(data[1]);
            Assert.That(endpoint, Is.EqualTo(Target));
            return NetSocketSendResult.Sent;
        }, n => new byte[n], _ => recycled++, (_, messages) => sent += messages);
        var buffer = new byte[] { 1, 42 };
        queue.Send(buffer, buffer.Length, Target, 3, 0);
        buffer[1] = 99;
        Assert.That(sent, Is.Zero);
        queue.Drain(.001);
        Assert.That(queue.Count, Is.EqualTo(1));
        blocked = false;
        queue.Drain(.002);
        Assert.Multiple(() =>
        {
            Assert.That(received, Is.EqualTo(new[] { 42 }));
            Assert.That(sent, Is.EqualTo(3));
            Assert.That(recycled, Is.EqualTo(1));
            Assert.That(queue.Bytes, Is.Zero);
            Assert.That(queue.Retried, Is.EqualTo(1));
            Assert.That(queue.WouldBlock, Is.EqualTo(2));
        });
    }

    [Test]
    public void ControlReserveAndPrioritySurviveDataQueueOverflow()
    {
        var blocked = true;
        var received = new List<byte>();
        var queue = new NetPendingSendQueue((data, _, _) =>
        {
            if (blocked) return NetSocketSendResult.WouldBlock;
            received.Add(data[0]);
            return NetSocketSendResult.Sent;
        }, n => new byte[n], _ => { }, (_, _) => { });
        var data = new byte[NetPendingSendQueue.MaximumBytes - NetPendingSendQueue.ControlReserveBytes];
        data[0] = 1;
        queue.Send(data, data.Length, Target, 1, 0);
        queue.Send(data, data.Length, Target, 1, 0);
        queue.Send(new byte[] { 134 }, 1, Target, 1, 0);
        blocked = false;
        queue.Drain(.001);
        Assert.That(received, Is.EqualTo(new byte[] { 134, 1 }));
        Assert.That(queue.Overflow, Is.EqualTo(1));
        Assert.That(queue.Bytes, Is.Zero);
    }

    [Test]
    public void ExpiryAndShutdownRecycleEveryBufferExactlyOnce()
    {
        var recycled = 0;
        var attempts = 0;
        var queue = new NetPendingSendQueue((_, _, _) =>
        {
            attempts++;
            return NetSocketSendResult.WouldBlock;
        }, n => new byte[n], _ => recycled++, (_, _) => Assert.Fail("Blocked packet counted as sent"));
        queue.Send(new byte[] { 1 }, 1, Target, 1, 0);
        queue.Send(new byte[] { 134 }, 1, Target, 1, 0);
        queue.Drain(NetPendingSendQueue.MaximumAge);
        Assert.That(attempts, Is.EqualTo(1));
        Assert.That(queue.Expired, Is.EqualTo(2));
        queue.Send(new byte[] { 1 }, 1, Target, 1, 1);
        queue.Clear();
        queue.Clear();
        Assert.That(recycled, Is.EqualTo(3));
        Assert.That(queue.Bytes, Is.Zero);
    }

    [Test]
    public void PermanentFailureIsNotRetriedOrCountedAsSent()
    {
        var queue = new NetPendingSendQueue((_, _, _) => NetSocketSendResult.Failed,
            _ => throw new Exception("Unexpected allocation"), _ => Assert.Fail("Unexpected recycle"),
            (_, _) => Assert.Fail("Failed send counted as sent"));
        queue.Send(new byte[] { 1 }, 1, Target, 1, 0);
        Assert.That(queue.Count, Is.Zero);
    }
}
