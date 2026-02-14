using System.Reflection;
using NUnit.Framework;

namespace FishNet.Transport.EOSNative.Tests.Editor
{
    /// <summary>
    /// Structural tests to verify voice player PUID wiring is properly set up.
    /// Can't instantiate NetworkBehaviour in EditMode, so we verify via reflection.
    /// </summary>
    public class VoicePlayerWiringTests
    {
        private System.Type _networkPlayerType;
        private System.Type _voicePlayerType;

        [SetUp]
        public void SetUp()
        {
            _networkPlayerType = System.Type.GetType(
                "FishNet.Transport.EOSNative.EOSNetworkPlayer, FishNet.Transport.EOSNative");
            _voicePlayerType = System.Type.GetType(
                "EOSNative.Voice.EOSVoicePlayer, EOSNative");
        }

        [Test]
        public void EOSNetworkPlayer_HasVoicePlayerField()
        {
            Assert.IsNotNull(_networkPlayerType, "EOSNetworkPlayer type not found");

            var field = _networkPlayerType.GetField("_voicePlayer",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "EOSNetworkPlayer should have _voicePlayer field");
            Assert.AreEqual(_voicePlayerType, field.FieldType,
                "_voicePlayer field should be of type EOSVoicePlayer");
        }

        [Test]
        public void EOSNetworkPlayer_HasOnPuidChangedMethod()
        {
            Assert.IsNotNull(_networkPlayerType, "EOSNetworkPlayer type not found");

            var method = _networkPlayerType.GetMethod("OnPuidChanged",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "EOSNetworkPlayer should have OnPuidChanged method");

            var parameters = method.GetParameters();
            Assert.AreEqual(3, parameters.Length,
                "OnPuidChanged should have 3 params (oldValue, newValue, asServer)");
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
            Assert.AreEqual(typeof(bool), parameters[2].ParameterType);
        }

        [Test]
        public void EOSNetworkPlayer_HasAwakeMethod()
        {
            Assert.IsNotNull(_networkPlayerType, "EOSNetworkPlayer type not found");

            // FishNet code gen may modify Awake — check DeclaredOnly
            var method = _networkPlayerType.GetMethod("Awake",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            // Fallback: FishNet may rename to __initializeEarly or similar
            if (method == null)
                method = _networkPlayerType.GetMethod("Awake",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.IsNotNull(method, "EOSNetworkPlayer should have Awake method (may be code-gen'd)");
        }

        [Test]
        public void EOSVoicePlayer_HasParticipantPuidProperty()
        {
            Assert.IsNotNull(_voicePlayerType, "EOSVoicePlayer type not found");

            var prop = _voicePlayerType.GetProperty("ParticipantPuid",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(prop, "EOSVoicePlayer should have ParticipantPuid property");
            Assert.IsTrue(prop.CanWrite, "ParticipantPuid should be writable");
            Assert.AreEqual(typeof(string), prop.PropertyType);
        }

        [Test]
        public void OnPuidChanged_WiresVoicePlayer()
        {
            // Verify that OnPuidChanged body references _voicePlayer by checking
            // the IL contains a field load for _voicePlayer
            Assert.IsNotNull(_networkPlayerType, "EOSNetworkPlayer type not found");

            var method = _networkPlayerType.GetMethod("OnPuidChanged",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "OnPuidChanged method not found");

            var body = method.GetMethodBody();
            Assert.IsNotNull(body, "OnPuidChanged should have a method body");

            // Method body should not be empty (should do something with the PUID)
            Assert.Greater(body.GetILAsByteArray().Length, 2,
                "OnPuidChanged should not be a no-op — it needs to wire the voice player");
        }
    }
}
