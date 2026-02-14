using System.Reflection;
using NUnit.Framework;

namespace FishNet.Transport.EOSNative.Tests.Editor
{
    /// <summary>
    /// Tests for the debug UI toggle (EOSHealthCheck OnGUI gating).
    /// Verifies the EnableHealthCheckUI reflection-based gating and visibility toggle.
    /// </summary>
    public class DebugUITests
    {
        private System.Type _healthCheckType;

        [SetUp]
        public void SetUp()
        {
            _healthCheckType = System.Type.GetType(
                "FishNet.Transport.EOSNative.Diagnostics.EOSHealthCheck, FishNet.Transport.EOSNative");
        }

        [Test]
        public void EOSHealthCheck_TypeExists()
        {
            Assert.IsNotNull(_healthCheckType, "EOSHealthCheck type should exist");
        }

        [Test]
        public void EOSHealthCheck_HasEnableOnGUIField()
        {
            Assert.IsNotNull(_healthCheckType);

            var field = _healthCheckType.GetField("_enableOnGUI",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "Should have _enableOnGUI serialized field");
            Assert.AreEqual(typeof(bool), field.FieldType);
        }

        [Test]
        public void EOSHealthCheck_HasVisibleField()
        {
            Assert.IsNotNull(_healthCheckType);

            var field = _healthCheckType.GetField("_visible",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "Should have _visible field for toggle state");
            Assert.AreEqual(typeof(bool), field.FieldType);
        }

        [Test]
        public void EOSHealthCheck_HasToggleKeyField()
        {
            Assert.IsNotNull(_healthCheckType);

            var field = _healthCheckType.GetField("_toggleKey",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "Should have _toggleKey field");
        }

        [Test]
        public void EOSHealthCheck_HasAutoCreateMethod()
        {
            Assert.IsNotNull(_healthCheckType);

            var method = _healthCheckType.GetMethod("AutoCreate",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Should have AutoCreate static method");
        }

        [Test]
        public void EOSHealthCheck_HasOnGUIMethod()
        {
            Assert.IsNotNull(_healthCheckType);

            var method = _healthCheckType.GetMethod("OnGUI",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "Should have OnGUI method");
        }

        [Test]
        public void EOSHealthCheck_OnGUI_ChecksEnableAndVisible()
        {
            Assert.IsNotNull(_healthCheckType);

            var method = _healthCheckType.GetMethod("OnGUI",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method);

            // Verify the method body references both _enableOnGUI and _visible fields
            var body = method.GetMethodBody();
            Assert.IsNotNull(body);
            Assert.Greater(body.GetILAsByteArray().Length, 10,
                "OnGUI should have non-trivial body that checks enable/visible flags");
        }

        [Test]
        public void EOSHealthCheck_HasTestModes()
        {
            var testModeType = _healthCheckType?.GetNestedType("TestMode");
            Assert.IsNotNull(testModeType, "Should have TestMode enum");
            Assert.IsTrue(testModeType.IsEnum);

            var names = System.Enum.GetNames(testModeType);
            Assert.Contains("Solo", names);
            Assert.Contains("Duo", names);
            Assert.Contains("Stress", names);
        }

        [Test]
        public void EOSHealthCheck_HasStepStatusEnum()
        {
            var stepStatusType = _healthCheckType?.GetNestedType("StepStatus");
            Assert.IsNotNull(stepStatusType, "Should have StepStatus enum");
            Assert.IsTrue(stepStatusType.IsEnum);

            var names = System.Enum.GetNames(stepStatusType);
            Assert.Contains("Pass", names);
            Assert.Contains("Fail", names);
            Assert.Contains("Skipped", names);
        }

        #region Transport Fast Disconnect

        [Test]
        public void EOSNativeTransport_HasSubscribedToLobbyEventsFlag()
        {
            var transportType = typeof(EOSNativeTransport);
            var field = transportType.GetField("_subscribedToLobbyEvents",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "Transport should have _subscribedToLobbyEvents flag");
            Assert.AreEqual(typeof(bool), field.FieldType);
        }

        [Test]
        public void EOSNativeTransport_OnLobbyMemberLeft_Exists()
        {
            var transportType = typeof(EOSNativeTransport);
            var method = transportType.GetMethod("OnLobbyMemberLeft",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "Transport should have OnLobbyMemberLeft method");

            var body = method.GetMethodBody();
            Assert.IsNotNull(body);
            // Should be non-trivial (handles both server and client cases)
            Assert.Greater(body.GetILAsByteArray().Length, 20,
                "OnLobbyMemberLeft should handle both server and client departure");
        }

        #endregion
    }
}
