using NUnit.Framework;
using FishNet.Transport.EOSNative;

namespace FishNet.Transport.EOSNative.Tests.Editor
{
    public class ConnectionTests
    {
        [Test]
        public void NewConnection_DefaultState_NothingOpened()
        {
            var conn = new Connection(1, null, null, default);
            Assert.IsFalse(conn.OpenedOutgoing);
            Assert.IsFalse(conn.OpenedIncoming);
            Assert.IsFalse(conn.IsFullyOpened);
        }

        [Test]
        public void Connection_OutgoingOnly_IsPendingOutgoing()
        {
            var conn = new Connection(1, null, null, default);
            conn.OpenedOutgoing = true;
            Assert.IsTrue(conn.IsPendingOutgoing);
            Assert.IsFalse(conn.IsPendingIncoming);
            Assert.IsFalse(conn.IsFullyOpened);
        }

        [Test]
        public void Connection_IncomingOnly_IsPendingIncoming()
        {
            var conn = new Connection(1, null, null, default);
            conn.OpenedIncoming = true;
            Assert.IsTrue(conn.IsPendingIncoming);
            Assert.IsFalse(conn.IsPendingOutgoing);
            Assert.IsFalse(conn.IsFullyOpened);
        }

        [Test]
        public void Connection_BothOpened_IsFullyOpened()
        {
            var conn = new Connection(1, null, null, default);
            conn.OpenedOutgoing = true;
            conn.OpenedIncoming = true;
            Assert.IsTrue(conn.IsFullyOpened);
            Assert.IsFalse(conn.IsPendingOutgoing);
            Assert.IsFalse(conn.IsPendingIncoming);
        }

        [Test]
        public void Connection_StoresConnectionId()
        {
            var conn = new Connection(42, null, null, default);
            Assert.AreEqual(42, conn.ConnectionId);
        }
    }
}
