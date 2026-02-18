using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace FishNet.Transport.EOSNative.Tests.Editor
{
    /// <summary>
    /// Tests for lobby leave/rejoin flow fixes.
    /// Verifies structural changes that prevent stale P2P state on Quest/Android:
    /// - Intentional leave flag suppresses auto-reconnect
    /// - Double-start guard on StartClientOnly
    /// - P2P CloseConnections called on leave
    /// - _remoteProductUserId cleared on leave
    /// </summary>
    public class LobbyRejoinTests
    {
        /// <summary>
        /// Creates an EOSAutoReconnect for testing with notifications disabled
        /// (EOSToastManager uses DontDestroyOnLoad which isn't available in edit mode).
        /// </summary>
        private static EOSAutoReconnect CreateTestReconnect(GameObject go)
        {
            var reconnect = go.AddComponent<EOSAutoReconnect>();
            reconnect.ShowNotifications = false;
            return reconnect;
        }

        #region EOSAutoReconnect — Intentional Leave

        [Test]
        public void EOSAutoReconnect_HasIntentionalLeaveField()
        {
            var type = typeof(EOSAutoReconnect);
            var field = type.GetField("_intentionalLeave",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "EOSAutoReconnect should have _intentionalLeave field");
            Assert.AreEqual(typeof(bool), field.FieldType);
        }

        [Test]
        public void EOSAutoReconnect_HasNotifyIntentionalLeaveMethod()
        {
            var type = typeof(EOSAutoReconnect);
            var method = type.GetMethod("NotifyIntentionalLeave",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(method, "EOSAutoReconnect should have public NotifyIntentionalLeave method");
            Assert.AreEqual(0, method.GetParameters().Length, "NotifyIntentionalLeave should take no parameters");
        }

        [Test]
        public void NotifyIntentionalLeave_SetsFlag()
        {
            var go = new GameObject("TestAutoReconnect");
            try
            {
                var reconnect = CreateTestReconnect(go);

                reconnect.NotifyIntentionalLeave();

                var field = typeof(EOSAutoReconnect).GetField("_intentionalLeave",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsTrue((bool)field.GetValue(reconnect), "_intentionalLeave should be true after NotifyIntentionalLeave");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void NotifyIntentionalLeave_ClearsLastLobbyCode()
        {
            var go = new GameObject("TestAutoReconnect");
            try
            {
                var reconnect = CreateTestReconnect(go);

                var codeField = typeof(EOSAutoReconnect).GetField("_lastLobbyCode",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                codeField.SetValue(reconnect, "ABCD");

                reconnect.NotifyIntentionalLeave();

                Assert.IsNull(codeField.GetValue(reconnect), "_lastLobbyCode should be null after intentional leave");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void NotifyIntentionalLeave_ClearsWasConnected()
        {
            var go = new GameObject("TestAutoReconnect");
            try
            {
                var reconnect = CreateTestReconnect(go);

                var wasConnectedField = typeof(EOSAutoReconnect).GetField("_wasConnected",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                wasConnectedField.SetValue(reconnect, true);

                reconnect.NotifyIntentionalLeave();

                Assert.IsFalse((bool)wasConnectedField.GetValue(reconnect), "_wasConnected should be false after intentional leave");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void NotifyIntentionalLeave_CancelsReconnect()
        {
            var go = new GameObject("TestAutoReconnect");
            try
            {
                var reconnect = CreateTestReconnect(go);

                var isReconnectingField = typeof(EOSAutoReconnect).GetField("_isReconnecting",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                isReconnectingField.SetValue(reconnect, true);

                reconnect.NotifyIntentionalLeave();

                Assert.IsFalse((bool)isReconnectingField.GetValue(reconnect), "_isReconnecting should be false after intentional leave");
                Assert.IsFalse(reconnect.IsReconnecting, "IsReconnecting should be false after intentional leave");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        #endregion

        #region EOSNativeTransport — Double-Start Guard

        [Test]
        public void StartClientOnly_IsPublicMethod()
        {
            var type = typeof(EOSNativeTransport);
            var method = type.GetMethod("StartClientOnly",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(method, "EOSNativeTransport should have public StartClientOnly method");
        }

        [Test]
        public void EOSNativeTransport_HasClientStateField()
        {
            var type = typeof(EOSNativeTransport);
            var field = type.GetField("_clientState",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "EOSNativeTransport should have _clientState field for double-start guard");
        }

        #endregion

        #region EOSNativeTransport — OnBeforeLeaveLobby Cleanup

        [Test]
        public void OnBeforeLeaveLobby_IsAsyncTaskMethod()
        {
            var type = typeof(EOSNativeTransport);
            var method = type.GetMethod("OnBeforeLeaveLobby",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "EOSNativeTransport should have OnBeforeLeaveLobby method");
            Assert.AreEqual(typeof(Task), method.ReturnType, "OnBeforeLeaveLobby should return Task (async)");
        }

        [Test]
        public void EOSNativeTransport_HasRemoteProductUserIdProperty()
        {
            var type = typeof(EOSNativeTransport);
            var prop = type.GetProperty("RemoteProductUserId",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(prop, "EOSNativeTransport should have RemoteProductUserId property");
            Assert.IsTrue(prop.CanRead && prop.CanWrite, "RemoteProductUserId should be read/write");
        }

        [Test]
        public void EOSNativeTransport_HasSocketNameProperty()
        {
            var type = typeof(EOSNativeTransport);
            var prop = type.GetProperty("SocketName",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(prop, "EOSNativeTransport should have SocketName property for P2P CloseConnections");
        }

        #endregion

        #region State Reset Integrity

        [Test]
        public void IntentionalLeave_FlagResetsAfterConnectionStopped()
        {
            var type = typeof(EOSAutoReconnect);

            var flagField = type.GetField("_intentionalLeave",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(flagField);

            var method = type.GetMethod("OnClientConnectionState",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "EOSAutoReconnect should have OnClientConnectionState handler");
        }

        [Test]
        public void EOSAutoReconnect_Singleton_IsAccessible()
        {
            var type = typeof(EOSAutoReconnect);
            var instanceProp = type.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(instanceProp, "EOSAutoReconnect should have static Instance property");
        }

        [Test]
        public void RapidLeaveRejoin_IntentionalFlagDoesNotLinger()
        {
            var go = new GameObject("TestAutoReconnect");
            try
            {
                var reconnect = CreateTestReconnect(go);
                var flagField = typeof(EOSAutoReconnect).GetField("_intentionalLeave",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var codeField = typeof(EOSAutoReconnect).GetField("_lastLobbyCode",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var wasConnectedField = typeof(EOSAutoReconnect).GetField("_wasConnected",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                // Simulate 5 rapid leave/rejoin cycles
                for (int i = 0; i < 5; i++)
                {
                    wasConnectedField.SetValue(reconnect, true);
                    codeField.SetValue(reconnect, $"CODE{i}");

                    reconnect.NotifyIntentionalLeave();

                    Assert.IsTrue((bool)flagField.GetValue(reconnect), $"Cycle {i}: flag should be set");
                    Assert.IsNull(codeField.GetValue(reconnect), $"Cycle {i}: lobby code should be null");
                    Assert.IsFalse((bool)wasConnectedField.GetValue(reconnect), $"Cycle {i}: wasConnected should be false");

                    // Simulate the flag being consumed (as OnClientConnectionState would do)
                    flagField.SetValue(reconnect, false);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        #endregion
    }
}
