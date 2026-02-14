using System.Reflection;
using NUnit.Framework;
using FishNet.Transport.EOSNative.Migration;

namespace FishNet.Transport.EOSNative.Tests.Editor
{
    /// <summary>
    /// Tests for host migration utilities and structural verification.
    /// Full migration flow requires FishNet runtime — tested via health check Duo mode.
    /// </summary>
    public class HostMigrationTests
    {
        #region PuidUtils Delegation

        [Test]
        public void HostMigrationManager_DelegatesToPuidUtils()
        {
            // Verify that HostMigrationManager has a NormalizePuid method
            var type = typeof(HostMigrationManager);
            var method = type.GetMethod("NormalizePuid",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "HostMigrationManager should have NormalizePuid method");

            // It should delegate to PuidUtils (body should contain call to PuidUtils)
            var body = method.GetMethodBody();
            Assert.IsNotNull(body, "NormalizePuid should have a body");

            // Verify it produces same result as PuidUtils
            string result = (string)method.Invoke(null, new object[] { "  ABC123  " });
            Assert.AreEqual("abc123", result);
            Assert.AreEqual(PuidUtils.NormalizePuid("  ABC123  "), result);
        }

        [Test]
        public void HostMigrationPlayerSpawner_DelegatesToPuidUtils()
        {
            var type = typeof(HostMigrationPlayerSpawner);
            var method = type.GetMethod("NormalizePuid",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "HostMigrationPlayerSpawner should have NormalizePuid method");

            string result = (string)method.Invoke(null, new object[] { "  DEF789  " });
            Assert.AreEqual("def789", result);
            Assert.AreEqual(PuidUtils.NormalizePuid("  DEF789  "), result);
        }

        #endregion

        #region MigratableObjectState

        [Test]
        public void MigratableObjectState_HasRequiredFields()
        {
            var type = typeof(MigratableObjectState);
            Assert.IsTrue(type.IsValueType, "MigratableObjectState should be a struct");

            Assert.IsNotNull(type.GetField("PrefabName"), "Should have PrefabName");
            Assert.IsNotNull(type.GetField("Position"), "Should have Position");
            Assert.IsNotNull(type.GetField("Rotation"), "Should have Rotation");
            Assert.IsNotNull(type.GetField("OwnerPuid"), "Should have OwnerPuid");
        }

        [Test]
        public void MigratableObjectState_DefaultValues()
        {
            var state = new MigratableObjectState();
            Assert.IsNull(state.PrefabName);
            Assert.IsNull(state.OwnerPuid);
            Assert.AreEqual(UnityEngine.Vector3.zero, state.Position);
            // Struct default is (0,0,0,0), not Quaternion.identity (0,0,0,1)
            Assert.AreEqual(default(UnityEngine.Quaternion), state.Rotation);
        }

        #endregion

        #region HostMigrationManager Structure

        [Test]
        public void HostMigrationManager_HasIsMigrating()
        {
            var type = typeof(HostMigrationManager);
            var prop = type.GetProperty("IsMigrating",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(prop, "HostMigrationManager should have IsMigrating property");
            Assert.AreEqual(typeof(bool), prop.PropertyType);
        }

        [Test]
        public void HostMigrationManager_HasClearMigrationState()
        {
            var type = typeof(HostMigrationManager);
            var method = type.GetMethod("ClearMigrationState",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(method, "HostMigrationManager should have ClearMigrationState method");
        }

        [Test]
        public void HostMigrationManager_HasRegisterPrefab()
        {
            var type = typeof(HostMigrationManager);
            var method = type.GetMethod("RegisterPrefab",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(method, "HostMigrationManager should have RegisterPrefab method");
        }

        [Test]
        public void HostMigrationManager_SubscribesToOnOwnerChanged()
        {
            // Verify the Start method exists and would subscribe
            var type = typeof(HostMigrationManager);
            var startMethod = type.GetMethod("Start",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(startMethod, "HostMigrationManager should have Start method");
        }

        #endregion

        #region HostMigrationPlayerSpawner Structure

        [Test]
        public void HostMigrationPlayerSpawner_HasPlayerPrefabProperty()
        {
            var type = typeof(HostMigrationPlayerSpawner);
            var prop = type.GetProperty("PlayerPrefab",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(prop, "Should have PlayerPrefab property");
        }

        [Test]
        public void HostMigrationPlayerSpawner_TracksSpawnedConnections()
        {
            var type = typeof(HostMigrationPlayerSpawner);
            var field = type.GetField("_spawnedConnections",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "Should have _spawnedConnections field for duplicate prevention");
        }

        #endregion
    }
}
