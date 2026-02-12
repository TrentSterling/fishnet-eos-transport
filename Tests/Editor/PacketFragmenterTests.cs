using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using FishNet.Transport.EOSNative;

namespace FishNet.Transport.EOSNative.Tests.Editor
{
    public class PacketFragmenterTests
    {
        private PacketFragmenter _fragmenter;

        [SetUp]
        public void SetUp()
        {
            _fragmenter = new PacketFragmenter();
        }

        [Test]
        public void Constants_AreCorrect()
        {
            Assert.AreEqual(7, PacketFragmenter.HeaderSize);
            Assert.AreEqual(1170, PacketFragmenter.MaxPacketSize);
            Assert.AreEqual(1163, PacketFragmenter.MaxPayloadSize);
        }

        [Test]
        public void NeedsFragmentation_SmallPacket_ReturnsFalse()
        {
            Assert.IsFalse(PacketFragmenter.NeedsFragmentation(100));
            Assert.IsFalse(PacketFragmenter.NeedsFragmentation(1163));
        }

        [Test]
        public void NeedsFragmentation_LargePacket_ReturnsTrue()
        {
            Assert.IsTrue(PacketFragmenter.NeedsFragmentation(1164));
            Assert.IsTrue(PacketFragmenter.NeedsFragmentation(5000));
        }

        [Test]
        public void Fragment_SmallData_ProducesSinglePacket()
        {
            byte[] data = new byte[100];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

            var packets = _fragmenter.Fragment(new ArraySegment<byte>(data)).ToList();

            Assert.AreEqual(1, packets.Count);
            Assert.AreEqual(100 + PacketFragmenter.HeaderSize, packets[0].Count);
        }

        [Test]
        public void Fragment_EmptyData_ProducesHeaderOnly()
        {
            byte[] data = new byte[0];
            var packets = _fragmenter.Fragment(new ArraySegment<byte>(data)).ToList();

            Assert.AreEqual(1, packets.Count);
            Assert.AreEqual(PacketFragmenter.HeaderSize, packets[0].Count);
        }

        [Test]
        public void Fragment_LargeData_ProducesMultiplePackets()
        {
            // 2400 bytes should need 3 fragments (1163 + 1163 + 74)
            byte[] data = new byte[2400];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

            var packets = _fragmenter.Fragment(new ArraySegment<byte>(data)).ToList();

            Assert.AreEqual(3, packets.Count);
            // First two should be max size
            Assert.AreEqual(PacketFragmenter.MaxPacketSize, packets[0].Count);
            Assert.AreEqual(PacketFragmenter.MaxPacketSize, packets[1].Count);
            // Last should be smaller
            Assert.AreEqual(2400 - 1163 * 2 + PacketFragmenter.HeaderSize, packets[2].Count);
        }

        [Test]
        public void Fragment_ThenReassemble_RoundTrips()
        {
            byte[] original = new byte[2400];
            var rng = new Random(42);
            rng.NextBytes(original);

            var packets = _fragmenter.Fragment(new ArraySegment<byte>(original)).ToList();

            var receiver = new PacketFragmenter();
            byte[] result = null;

            foreach (var packet in packets)
            {
                result = receiver.ProcessIncoming(1, packet, 0);
            }

            Assert.IsNotNull(result, "Should reassemble after all fragments received");
            Assert.AreEqual(original.Length, result.Length);
            CollectionAssert.AreEqual(original, result);
        }

        [Test]
        public void Fragment_SinglePacket_RoundTrips()
        {
            byte[] original = new byte[] { 1, 2, 3, 4, 5 };

            var packets = _fragmenter.Fragment(new ArraySegment<byte>(original)).ToList();
            Assert.AreEqual(1, packets.Count);

            var receiver = new PacketFragmenter();
            byte[] result = receiver.ProcessIncoming(1, packets[0], 0);

            Assert.IsNotNull(result);
            CollectionAssert.AreEqual(original, result);
        }

        [Test]
        public void ProcessIncoming_TooSmall_ReturnsNull()
        {
            byte[] tooSmall = new byte[3]; // Less than header size
            var result = _fragmenter.ProcessIncoming(1, new ArraySegment<byte>(tooSmall), 0);
            Assert.IsNull(result);
        }

        [Test]
        public void ProcessIncoming_PartialFragments_ReturnsNull()
        {
            // Send a large packet but only deliver first fragment
            byte[] data = new byte[2400];
            var packets = _fragmenter.Fragment(new ArraySegment<byte>(data)).ToList();

            var receiver = new PacketFragmenter();
            // Only send first fragment
            byte[] result = receiver.ProcessIncoming(1, packets[0], 0);
            Assert.IsNull(result, "Should not reassemble with incomplete fragments");
        }

        [Test]
        public void CreateSinglePacket_AddsHeader()
        {
            byte[] data = new byte[] { 0xAA, 0xBB, 0xCC };
            var packet = _fragmenter.CreateSinglePacket(new ArraySegment<byte>(data));

            Assert.AreEqual(PacketFragmenter.HeaderSize + 3, packet.Count);

            // Verify payload at offset HeaderSize
            Assert.AreEqual(0xAA, packet.Array[packet.Offset + PacketFragmenter.HeaderSize]);
            Assert.AreEqual(0xBB, packet.Array[packet.Offset + PacketFragmenter.HeaderSize + 1]);
            Assert.AreEqual(0xCC, packet.Array[packet.Offset + PacketFragmenter.HeaderSize + 2]);
        }

        [Test]
        public void Fragment_ExactMaxPayload_ProducesSinglePacket()
        {
            byte[] data = new byte[PacketFragmenter.MaxPayloadSize]; // Exactly 1163
            var packets = _fragmenter.Fragment(new ArraySegment<byte>(data)).ToList();
            Assert.AreEqual(1, packets.Count);
        }

        [Test]
        public void Fragment_MaxPayloadPlusOne_ProducesTwoPackets()
        {
            byte[] data = new byte[PacketFragmenter.MaxPayloadSize + 1]; // 1164
            var packets = _fragmenter.Fragment(new ArraySegment<byte>(data)).ToList();
            Assert.AreEqual(2, packets.Count);
        }
    }
}
