using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using EOSNative;
using EOSNative.Lobbies;
using EOSNative.Voice;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transport.EOSNative.Migration;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace FishNet.Transport.EOSNative.Diagnostics
{
    /// <summary>
    /// Automated runtime health check for the EOS + FishNet stack.
    /// Three test tiers: Solo (single instance), Duo (two instances), Stress (duo + host migration).
    /// Auto-attaches to the EOSNativeTransport or EOSManager GameObject at runtime.
    /// Press F11 to toggle the panel, select mode, press "Run" to execute.
    /// Uses OnGUI — no Canvas or prefab setup needed.
    /// </summary>
    public class EOSHealthCheck : MonoBehaviour
    {
        #region Auto-Create

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            // Gated by EOSManager.EnableHealthCheckUI if the property exists (eos-sdk >= 1.4.2).
            // Uses reflection to avoid a hard compile-time dependency on a specific eos-sdk version.
            var eosManager = FindAnyObjectByType<EOSManager>();
            if (eosManager != null)
            {
                var prop = eosManager.GetType().GetProperty("EnableHealthCheckUI",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null && !(bool)prop.GetValue(eosManager))
                    return;
            }

            if (FindAnyObjectByType<EOSHealthCheck>() != null)
                return;

            var transport = FindAnyObjectByType<EOSNativeTransport>();
            if (transport != null)
            {
                transport.gameObject.AddComponent<EOSHealthCheck>();
                return;
            }

            if (eosManager != null)
            {
                eosManager.gameObject.AddComponent<EOSHealthCheck>();
                return;
            }
        }

        #endregion

        #region Types

        public enum StepStatus { Pending, Running, Pass, Warning, Fail, Skipped }
        public enum TestMode { Solo, Duo, Stress }

        public class Step
        {
            public string Name;
            public StepStatus Status;
            public string Detail;
            public long ElapsedMs;
        }

        private enum TestPhase { Idle, Running, Done }
        private enum DuoRole { Unknown, Host, Client }

        #endregion

        #region Constants

        private const string HC_LOBBY_ATTR = "HEALTHCHECK";

        #endregion

        #region Settings

        [Header("Display")]
        [Tooltip("Enable or disable the OnGUI overlay rendering.")]
        [SerializeField] private bool _enableOnGUI = true;

        [Tooltip("Keyboard shortcut to toggle the panel.")]
        [SerializeField] private Key _toggleKey = Key.F11;

        [Tooltip("Panel width in pixels.")]
        [SerializeField] private float _panelWidth = 400f;

        [Header("Test Settings")]
        [Tooltip("Which test tier to run.")]
        [SerializeField] private TestMode _testMode = TestMode.Solo;

        [Tooltip("Timeout per step in seconds.")]
        [SerializeField] private float _stepTimeout = 10f;

        [Tooltip("How long to hold the connected state before teardown (seconds).")]
        [SerializeField] private float _holdDuration = 2f;

        [Tooltip("Auto-run health check on Start (after auto-init delay).")]
        [SerializeField] private bool _autoRunOnStart = false;

        [Tooltip("Seconds to wait before auto-running (lets auto-init finish).")]
        [SerializeField] private float _autoRunDelay = 3f;

        [Header("Duo/Stress Settings")]
        [Tooltip("How long to wait for a partner to join (host) or for a lobby to appear (client).")]
        [SerializeField] private float _duoPartnerTimeout = 30f;

        [Tooltip("How long to wait for host migration to complete (Stress client).")]
        [SerializeField] private float _migrationTimeout = 15f;

        #endregion

        #region State

        private bool _visible = true;
        private TestPhase _phase = TestPhase.Idle;
        private readonly List<Step> _steps = new();
        private int _passCount;
        private int _failCount;
        private int _skipCount;
        private float _totalElapsed;
        private bool _isRunning;
        private DuoRole _duoRole = DuoRole.Unknown;

        // Cached refs
        private NetworkManager _networkManager;
        private EOSNativeTransport _transport;

        // GUI
        private Vector2 _scrollPos;
        private GUIStyle _boxStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _rowStyle;
        private GUIStyle _detailStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _summaryStyle;
        private GUIStyle _modeButtonStyle;
        private GUIStyle _modeActiveStyle;
        private bool _stylesBuilt;

        // Colors
        private static readonly Color Green  = new(0.2f, 0.9f, 0.2f);
        private static readonly Color Yellow = new(1f, 0.85f, 0.1f);
        private static readonly Color Red    = new(1f, 0.25f, 0.2f);
        private static readonly Color Gray   = new(0.5f, 0.5f, 0.5f);
        private static readonly Color Cyan   = new(0.3f, 0.85f, 1f);

        #endregion

        #region Lifecycle

        private async void Start()
        {
            if (_autoRunOnStart)
            {
                await Task.Delay(Mathf.RoundToInt(_autoRunDelay * 1000));
                if (this != null && gameObject.activeInHierarchy)
                    RunHealthCheck();
            }
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current[_toggleKey].wasPressedThisFrame)
                _visible = !_visible;
        }

        #endregion

        #region Public API

        public void RunHealthCheck()
        {
            if (_isRunning) return;
            _ = RunHealthCheckAsync();
        }

        #endregion

        #region Role Detection

        private static bool IsParrelSyncClone()
        {
#if UNITY_EDITOR
            try
            {
                var clonesManagerType = Type.GetType("ParrelSync.ClonesManager, ParrelSync");
                if (clonesManagerType == null)
                    return false;

                var isCloneMethod = clonesManagerType.GetMethod("IsClone",
                    BindingFlags.Public | BindingFlags.Static);
                if (isCloneMethod == null)
                    return false;

                return (bool)isCloneMethod.Invoke(null, null);
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }

        private void DetermineRole()
        {
            if (_testMode == TestMode.Solo)
            {
                _duoRole = DuoRole.Host;
                return;
            }

            // In editor: ParrelSync clones are clients
            if (IsParrelSyncClone())
            {
                _duoRole = DuoRole.Client;
                return;
            }

            // In builds: --healthcheck-client CLI arg means client
            var args = Environment.GetCommandLineArgs();
            foreach (var arg in args)
            {
                if (arg.Equals("--healthcheck-client", StringComparison.OrdinalIgnoreCase))
                {
                    _duoRole = DuoRole.Client;
                    return;
                }
            }

            _duoRole = DuoRole.Host;
        }

        #endregion

        #region Test Sequence — Main

        private async Task RunHealthCheckAsync()
        {
            _isRunning = true;
            _phase = TestPhase.Running;
            _visible = true;
            _steps.Clear();
            _passCount = 0;
            _failCount = 0;
            _skipCount = 0;

            var totalSw = Stopwatch.StartNew();

            // Cache refs
            _networkManager = FindAnyObjectByType<NetworkManager>();
            _transport = _networkManager != null
                ? _networkManager.TransportManager?.Transport as EOSNativeTransport
                : FindAnyObjectByType<EOSNativeTransport>();
            if (_transport == null)
                _transport = FindAnyObjectByType<EOSNativeTransport>();

            DetermineRole();

            // Steps 1-8: common setup
            bool loggedIn = await RunCommonSetupSteps();

            // Dispatch to mode-specific steps
            switch (_testMode)
            {
                case TestMode.Solo:
                    await RunSoloSteps(loggedIn);
                    break;
                case TestMode.Duo:
                case TestMode.Stress:
                    if (_duoRole == DuoRole.Client)
                        await RunDuoClientSteps(loggedIn);
                    else
                        await RunDuoHostSteps(loggedIn);
                    break;
            }

            // Finalize
            totalSw.Stop();
            _totalElapsed = totalSw.ElapsedMilliseconds / 1000f;
            _phase = TestPhase.Done;
            _isRunning = false;

            string modeLabel = _testMode == TestMode.Solo ? "Solo" : $"{_testMode}:{_duoRole}";
            string summary = $"[EOSHealthCheck:{modeLabel}] Done: {_passCount} pass, {_failCount} fail, {_skipCount} skip in {_totalElapsed:F1}s";
            if (_failCount > 0)
                Debug.LogWarning(summary);
            else
                Debug.Log(summary);
        }

        /// <summary>
        /// Steps 1-8: Scene setup, EOSManager, SDK init, login, lobby manager, P2P, migration manager, player spawner.
        /// </summary>
        private async Task<bool> RunCommonSetupSteps()
        {
            // Step 1: Scene Setup
            await RunStep("Scene Setup", () =>
            {
                if (_networkManager == null)
                    return (StepStatus.Fail, "NetworkManager not found");
                if (_transport == null)
                    return (StepStatus.Fail, "EOSNativeTransport not found");

                var wiredTransport = _networkManager.TransportManager?.Transport;
                if (wiredTransport != _transport)
                    return (StepStatus.Fail, $"Transport mismatch: {wiredTransport?.GetType().Name ?? "null"}");

                return (StepStatus.Pass, "NM + Transport OK");
            });

            // Step 2: EOSManager
            await RunStep("EOSManager", () =>
            {
                if (EOSManager.Instance == null)
                    return (StepStatus.Fail, "EOSManager.Instance is null");
                return (StepStatus.Pass, "Instance exists");
            });

            // Step 3: SDK Initialized
            bool sdkInit = false;
            await RunStep("SDK Initialized", () =>
            {
                if (EOSManager.Instance == null)
                    return (StepStatus.Skipped, "No EOSManager");
                sdkInit = EOSManager.Instance.IsInitialized;
                if (!sdkInit)
                    return (StepStatus.Fail, "Not initialized (auto-init disabled or failed?)");
                return (StepStatus.Pass, "Initialized");
            });

            // Step 4: EOS Login
            bool loggedIn = false;
            await RunStep("EOS Login", () =>
            {
                if (!sdkInit)
                    return (StepStatus.Skipped, "SDK not init");
                loggedIn = EOSManager.Instance.IsLoggedIn;
                if (!loggedIn)
                    return (StepStatus.Fail, "Not logged in");
                string puid = EOSManager.Instance.LocalProductUserId?.ToString() ?? "?";
                return (StepStatus.Pass, Truncate(puid, 16));
            });

            // Step 5: LobbyManager
            await RunStep("LobbyManager", () =>
            {
                if (_transport == null)
                    return (StepStatus.Skipped, "No transport");
                var lm = _transport.LobbyManager;
                if (lm == null)
                    return (StepStatus.Fail, "LobbyManager is null");
                return (StepStatus.Pass, "Instance OK");
            });

            // Step 6: P2P Interface
            await RunStep("P2P Interface", () =>
            {
                if (!loggedIn)
                    return (StepStatus.Skipped, "Not logged in");
                var p2p = EOSManager.Instance.P2PInterface;
                if (p2p == null)
                    return (StepStatus.Fail, "P2PInterface is null");
                return (StepStatus.Pass, "Available");
            });

            // Step 7: HostMigrationManager
            await RunStep("HostMigrationManager", () =>
            {
                var hm = HostMigrationManager.Instance;
                if (hm == null)
                    return (StepStatus.Warning, "Not found (optional)");
                return (StepStatus.Pass, "Instance OK");
            });

            // Step 8: PlayerSpawner
            await RunStep("PlayerSpawner", () =>
            {
                var spawner = FindAnyObjectByType<HostMigrationPlayerSpawner>();
                if (spawner == null)
                    return (StepStatus.Warning, "Not found (optional)");
                if (spawner.PlayerPrefab == null)
                    return (StepStatus.Warning, "No prefab assigned");
                return (StepStatus.Pass, $"Prefab: {spawner.PlayerPrefab.name}");
            });

            return loggedIn;
        }

        #endregion

        #region Solo Steps

        private async Task RunSoloSteps(bool loggedIn)
        {
            bool lobbyCreated = false;

            // Step 9: Host Lobby
            if (loggedIn && _transport != null)
            {
                await RunStepAsync("Host Lobby", async () =>
                {
                    if (_transport.IsInLobby)
                        return (StepStatus.Skipped, $"Already in lobby: {_transport.CurrentLobby.JoinCode}");

                    var (result, lobby) = await _transport.HostLobbyAsync($"HC{UnityEngine.Random.Range(1000, 9999)}");
                    if (result == Epic.OnlineServices.Result.Success)
                    {
                        lobbyCreated = true;
                        return (StepStatus.Pass, $"Code: {lobby.JoinCode}");
                    }
                    return (StepStatus.Fail, $"Result: {result}");
                });
            }
            else
            {
                AddSkipped("Host Lobby", "Not logged in");
            }

            // Step 10: Server Started
            if (lobbyCreated)
            {
                await RunStepWithWait("Server Started", () =>
                {
                    if (_transport == null) return (StepStatus.Fail, "No transport");
                    var state = _transport.GetConnectionState(true);
                    if (state == LocalConnectionState.Started) return (StepStatus.Pass, "Running");
                    if (state == LocalConnectionState.Starting) return (StepStatus.Running, "Starting...");
                    return (StepStatus.Fail, $"State: {state}");
                });
            }
            else
            {
                AddSkipped("Server Started", "No lobby");
            }

            // Step 11: Client (Host) Started
            if (lobbyCreated)
            {
                await RunStepWithWait("Client (Host) Started", () =>
                {
                    if (_transport == null) return (StepStatus.Fail, "No transport");
                    var state = _transport.GetConnectionState(false);
                    if (state == LocalConnectionState.Started)
                        return (StepStatus.Pass, _transport.HasClientHost ? "ClientHost active" : "Connected");
                    if (state == LocalConnectionState.Starting) return (StepStatus.Running, "Connecting...");
                    return (StepStatus.Fail, $"State: {state}");
                });
            }
            else
            {
                AddSkipped("Client (Host) Started", "No lobby");
            }

            // Step 12: Lobby State
            if (lobbyCreated)
            {
                await RunStep("Lobby State", () =>
                {
                    if (!_transport.IsInLobby) return (StepStatus.Fail, "Not in lobby");
                    if (!_transport.IsLobbyOwner) return (StepStatus.Fail, "Not owner");
                    var lobby = _transport.CurrentLobby;
                    return (StepStatus.Pass, $"Owner, {lobby.MemberCount}/{lobby.MaxMembers} members");
                });
            }
            else
            {
                AddSkipped("Lobby State", "No lobby");
            }

            // Step 13: Connection Hold
            if (lobbyCreated)
            {
                await RunStepAsync("Connection Hold", async () =>
                {
                    long bytesBefore = _transport.TotalBytesSent + _transport.TotalBytesReceived;
                    await Task.Delay(Mathf.RoundToInt(_holdDuration * 1000));
                    long bytesAfter = _transport.TotalBytesSent + _transport.TotalBytesReceived;
                    long delta = bytesAfter - bytesBefore;

                    bool serverOk = _transport.GetConnectionState(true) == LocalConnectionState.Started;
                    bool clientOk = _transport.GetConnectionState(false) == LocalConnectionState.Started;

                    if (!serverOk || !clientOk)
                        return (StepStatus.Fail, $"Connection dropped! S:{serverOk} C:{clientOk}");

                    if (delta > 0)
                        return (StepStatus.Pass, $"Stable {_holdDuration}s, {delta} bytes exchanged");
                    return (StepStatus.Pass, $"Stable {_holdDuration}s (no traffic — normal for solo host)");
                });
            }
            else
            {
                AddSkipped("Connection Hold", "No lobby");
            }

            // Step 14: Player Spawn
            if (lobbyCreated)
            {
                await RunStepWithWait("Player Spawn", () =>
                {
                    var local = EOSNetworkPlayer.LocalPlayer;
                    if (local != null)
                        return (StepStatus.Pass, $"Spawned (conn:{local.ConnectionId})");
                    if (EOSNetworkPlayer.PlayerCount > 0)
                        return (StepStatus.Running, $"{EOSNetworkPlayer.PlayerCount} player(s), no local owner yet");
                    return (StepStatus.Running, "Waiting for spawn...");
                });
            }
            else
            {
                AddSkipped("Player Spawn", "No lobby");
            }

            // Step 15: Player SyncVars
            if (lobbyCreated)
            {
                await RunStepWithWait("Player SyncVars", () =>
                {
                    var local = EOSNetworkPlayer.LocalPlayer;
                    if (local == null)
                        return (StepStatus.Running, "No local player yet");
                    if (!string.IsNullOrEmpty(local.Puid))
                        return (StepStatus.Pass, $"PUID: {Truncate(local.Puid, 12)}, Name: {local.DisplayName}");
                    return (StepStatus.Running, "Waiting for PUID sync...");
                });
            }
            else
            {
                AddSkipped("Player SyncVars", "No lobby");
            }

            // Step 16: Voice Connected
            if (lobbyCreated)
            {
                await RunStepWithWait("Voice Connected", () =>
                {
                    var voice = EOSVoiceManager.Instance;
                    if (voice == null)
                        return (StepStatus.Pass, "No EOSVoiceManager (optional)");
                    if (voice.IsConnected)
                        return (StepStatus.Pass, $"Connected, {voice.ParticipantCount} participant(s)");
                    if (voice.IsVoiceEnabled)
                        return (StepStatus.Running, "Connecting to RTC...");
                    return (StepStatus.Pass, "Voice not enabled for this lobby");
                });
            }
            else
            {
                AddSkipped("Voice Connected", "No lobby");
            }

            // Step 17: Voice Player Wired
            if (lobbyCreated)
            {
                await RunStepWithWait("Voice Player Wired", () =>
                {
                    var local = EOSNetworkPlayer.LocalPlayer;
                    if (local == null)
                        return (StepStatus.Running, "No local player yet");
                    var vp = local.GetComponent<EOSVoicePlayer>();
                    if (vp == null)
                        return (StepStatus.Fail, "No EOSVoicePlayer on local player");
                    if (string.IsNullOrEmpty(vp.ParticipantPuid))
                        return (StepStatus.Running, "PUID not set yet");
                    if (vp.ParticipantPuid == local.Puid)
                        return (StepStatus.Pass, $"PUID: {Truncate(vp.ParticipantPuid, 12)}, Spatial: {vp.SpatialBlend:F1}");
                    return (StepStatus.Fail, $"PUID mismatch: player={Truncate(local.Puid, 8)} voice={Truncate(vp.ParticipantPuid, 8)}");
                });
            }
            else
            {
                AddSkipped("Voice Player Wired", "No lobby");
            }

            // Step 18: Voice Mic Level (info only, always passes)
            if (lobbyCreated)
            {
                await RunStep("Voice Mic Level", () =>
                {
                    var voice = EOSVoiceManager.Instance;
                    if (voice == null)
                        return (StepStatus.Pass, "No voice manager");
                    return (StepStatus.Pass, $"Level: {voice.LocalMicLevel:F2}, Status: {voice.LocalAudioStatus}, Muted: {voice.IsMuted}");
                });
            }
            else
            {
                AddSkipped("Voice Mic Level", "No lobby");
            }

            // Step 18: Teardown
            if (lobbyCreated)
            {
                await RunTeardownStep();
            }
            else
            {
                AddSkipped("Teardown", "Nothing to tear down");
            }
        }

        #endregion

        #region Duo Host Steps

        private async Task RunDuoHostSteps(bool loggedIn)
        {
            bool lobbyCreated = false;

            // Step 9: Host Lobby with HC attribute
            if (loggedIn && _transport != null)
            {
                await RunStepAsync("Host Lobby [HC]", async () =>
                {
                    if (_transport.IsInLobby)
                        return (StepStatus.Skipped, $"Already in lobby: {_transport.CurrentLobby.JoinCode}");

                    var options = new LobbyCreateOptions
                    {
                        Attributes = new Dictionary<string, string>
                        {
                            [HC_LOBBY_ATTR] = "true"
                        }
                    };

                    var (result, lobby) = await _transport.HostLobbyAsync(options);
                    if (result == Epic.OnlineServices.Result.Success)
                    {
                        lobbyCreated = true;
                        return (StepStatus.Pass, $"Code: {lobby.JoinCode}");
                    }
                    return (StepStatus.Fail, $"Result: {result}");
                });
            }
            else
            {
                AddSkipped("Host Lobby [HC]", "Not logged in");
            }

            // Step 10: Server Started
            if (lobbyCreated)
            {
                await RunStepWithWait("Server Started", () =>
                {
                    if (_transport == null) return (StepStatus.Fail, "No transport");
                    var state = _transport.GetConnectionState(true);
                    if (state == LocalConnectionState.Started) return (StepStatus.Pass, "Running");
                    if (state == LocalConnectionState.Starting) return (StepStatus.Running, "Starting...");
                    return (StepStatus.Fail, $"State: {state}");
                });
            }
            else
            {
                AddSkipped("Server Started", "No lobby");
            }

            // Step 11: Client (Host) Started
            if (lobbyCreated)
            {
                await RunStepWithWait("Client (Host) Started", () =>
                {
                    if (_transport == null) return (StepStatus.Fail, "No transport");
                    var state = _transport.GetConnectionState(false);
                    if (state == LocalConnectionState.Started)
                        return (StepStatus.Pass, _transport.HasClientHost ? "ClientHost active" : "Connected");
                    if (state == LocalConnectionState.Starting) return (StepStatus.Running, "Connecting...");
                    return (StepStatus.Fail, $"State: {state}");
                });
            }
            else
            {
                AddSkipped("Client (Host) Started", "No lobby");
            }

            // Step 12: Lobby State
            if (lobbyCreated)
            {
                await RunStep("Lobby State", () =>
                {
                    if (!_transport.IsInLobby) return (StepStatus.Fail, "Not in lobby");
                    if (!_transport.IsLobbyOwner) return (StepStatus.Fail, "Not owner");
                    var lobby = _transport.CurrentLobby;
                    return (StepStatus.Pass, $"Owner, {lobby.MemberCount}/{lobby.MaxMembers} members");
                });
            }
            else
            {
                AddSkipped("Lobby State", "No lobby");
            }

            // Step 13: Wait for Partner (30s timeout)
            if (lobbyCreated)
            {
                await RunStepWithWait("Wait for Partner", () =>
                {
                    if (!_transport.IsInLobby) return (StepStatus.Fail, "Lost lobby");
                    var lobby = _transport.CurrentLobby;
                    if (lobby.MemberCount >= 2)
                        return (StepStatus.Pass, $"{lobby.MemberCount} members joined");
                    return (StepStatus.Running, $"Waiting... ({lobby.MemberCount}/2)");
                }, _duoPartnerTimeout);
            }
            else
            {
                AddSkipped("Wait for Partner", "No lobby");
            }

            // Step 14: Remote P2P
            if (lobbyCreated)
            {
                await RunStepWithWait("Remote P2P", () =>
                {
                    int count = _transport.ServerP2PConnectionCount;
                    if (count >= 1)
                        return (StepStatus.Pass, $"{count} remote connection(s)");
                    return (StepStatus.Running, "Waiting for P2P...");
                });
            }
            else
            {
                AddSkipped("Remote P2P", "No lobby");
            }

            // Step 15: Remote Player
            if (lobbyCreated)
            {
                await RunStepWithWait("Remote Player", () =>
                {
                    int count = EOSNetworkPlayer.PlayerCount;
                    if (count >= 2)
                        return (StepStatus.Pass, $"{count} players tracked");
                    return (StepStatus.Running, $"{count}/2 players...");
                });
            }
            else
            {
                AddSkipped("Remote Player", "No lobby");
            }

            // Step 16: SyncVars Verified
            if (lobbyCreated)
            {
                await RunStepWithWait("SyncVars Verified", () =>
                {
                    var local = EOSNetworkPlayer.LocalPlayer;
                    if (local == null)
                        return (StepStatus.Running, "No local player yet");
                    if (!string.IsNullOrEmpty(local.Puid))
                        return (StepStatus.Pass, $"PUID: {Truncate(local.Puid, 12)}, Name: {local.DisplayName}");
                    return (StepStatus.Running, "Waiting for PUID sync...");
                });
            }
            else
            {
                AddSkipped("SyncVars Verified", "No lobby");
            }

            // Step 17: Voice Connected
            if (lobbyCreated)
            {
                await RunStepWithWait("Voice Connected", () => CheckVoiceConnected());
            }
            else
            {
                AddSkipped("Voice Connected", "No lobby");
            }

            // Step 18: Voice Players Wired
            if (lobbyCreated)
            {
                await RunStepWithWait("Voice Players Wired", () => CheckVoicePlayersWired());
            }
            else
            {
                AddSkipped("Voice Players Wired", "No lobby");
            }

            // Step 17: Stability Hold
            if (lobbyCreated)
            {
                await RunStepAsync("Stability Hold", async () =>
                {
                    long bytesBefore = _transport.TotalBytesSent + _transport.TotalBytesReceived;
                    await Task.Delay(Mathf.RoundToInt(_holdDuration * 1000));
                    long bytesAfter = _transport.TotalBytesSent + _transport.TotalBytesReceived;
                    long delta = bytesAfter - bytesBefore;

                    bool serverOk = _transport.GetConnectionState(true) == LocalConnectionState.Started;
                    bool clientOk = _transport.GetConnectionState(false) == LocalConnectionState.Started;
                    bool p2pOk = _transport.ServerP2PConnectionCount >= 1;

                    if (!serverOk || !clientOk)
                        return (StepStatus.Fail, $"Connection dropped! S:{serverOk} C:{clientOk}");
                    if (!p2pOk)
                        return (StepStatus.Fail, "P2P connection lost");

                    return (StepStatus.Pass, $"Stable {_holdDuration}s, {delta} bytes, P2P OK");
                });
            }
            else
            {
                AddSkipped("Stability Hold", "No lobby");
            }

            // Stress mode: migration save + host leave
            if (_testMode == TestMode.Stress && lobbyCreated)
            {
                // Step 18: Migration Save
                await RunStep("Migration Save", () =>
                {
                    var hm = HostMigrationManager.Instance;
                    if (hm == null)
                        return (StepStatus.Fail, "No HostMigrationManager");
                    hm.DebugTriggerSave();
                    return (StepStatus.Pass, $"Saved, {hm.TotalTrackedCount} tracked objects");
                });

                // Step 19: Host Leave (triggers migration for partner)
                await RunStepAsync("Host Leave", async () =>
                {
                    try
                    {
                        await _transport.LeaveLobbyAsync();
                        await Task.Delay(500);
                        bool leftLobby = !_transport.IsInLobby;
                        return leftLobby
                            ? (StepStatus.Pass, "Left — partner should migrate")
                            : (StepStatus.Fail, "Still in lobby");
                    }
                    catch (Exception ex)
                    {
                        return (StepStatus.Fail, $"Exception: {ex.Message}");
                    }
                });
            }
            else if (lobbyCreated)
            {
                // Step 18: Normal teardown
                await RunTeardownStep();
            }
            else
            {
                AddSkipped(_testMode == TestMode.Stress ? "Migration Save" : "Teardown", "Nothing to tear down");
            }
        }

        #endregion

        #region Duo Client Steps

        private async Task RunDuoClientSteps(bool loggedIn)
        {
            bool joined = false;
            string foundJoinCode = null;

            // Step 9: Find HC Lobby (polls every 2s)
            if (loggedIn && _transport != null)
            {
                await RunStepAsync("Find HC Lobby", async () =>
                {
                    float deadline = Time.realtimeSinceStartup + _duoPartnerTimeout;
                    while (Time.realtimeSinceStartup < deadline)
                    {
                        var searchOptions = new LobbySearchOptions().WithAttribute(HC_LOBBY_ATTR, "true");
                        var (result, lobbies) = await _transport.SearchLobbiesAsync(searchOptions);
                        if (result == Epic.OnlineServices.Result.Success && lobbies != null && lobbies.Count > 0)
                        {
                            foundJoinCode = lobbies[0].JoinCode;
                            return (StepStatus.Pass, $"Found: {foundJoinCode} ({lobbies.Count} result(s))");
                        }
                        await Task.Delay(2000);
                    }
                    return (StepStatus.Fail, $"Timeout ({_duoPartnerTimeout}s) — no HC lobby found");
                }, _duoPartnerTimeout + 5f);
            }
            else
            {
                AddSkipped("Find HC Lobby", "Not logged in");
            }

            // Step 10: Join Lobby
            if (foundJoinCode != null && _transport != null)
            {
                await RunStepAsync("Join Lobby", async () =>
                {
                    if (_transport.IsInLobby)
                        return (StepStatus.Skipped, $"Already in lobby: {_transport.CurrentLobby.JoinCode}");

                    var (result, lobby) = await _transport.JoinLobbyAsync(foundJoinCode);
                    if (result == Epic.OnlineServices.Result.Success)
                    {
                        joined = true;
                        return (StepStatus.Pass, $"Joined: {lobby.JoinCode}");
                    }
                    return (StepStatus.Fail, $"Result: {result}");
                });
            }
            else
            {
                AddSkipped("Join Lobby", foundJoinCode == null ? "No lobby found" : "No transport");
            }

            // Step 11: Client Connected
            if (joined)
            {
                await RunStepWithWait("Client Connected", () =>
                {
                    if (_transport == null) return (StepStatus.Fail, "No transport");
                    var state = _transport.GetConnectionState(false);
                    if (state == LocalConnectionState.Started)
                        return (StepStatus.Pass, "Connected to host");
                    if (state == LocalConnectionState.Starting)
                        return (StepStatus.Running, "Connecting...");
                    return (StepStatus.Fail, $"State: {state}");
                });
            }
            else
            {
                AddSkipped("Client Connected", "Not joined");
            }

            // Step 12: Player Spawn
            if (joined)
            {
                await RunStepWithWait("Player Spawn", () =>
                {
                    var local = EOSNetworkPlayer.LocalPlayer;
                    if (local != null)
                        return (StepStatus.Pass, $"Spawned (conn:{local.ConnectionId})");
                    if (EOSNetworkPlayer.PlayerCount > 0)
                        return (StepStatus.Running, $"{EOSNetworkPlayer.PlayerCount} player(s), no local owner yet");
                    return (StepStatus.Running, "Waiting for spawn...");
                });
            }
            else
            {
                AddSkipped("Player Spawn", "Not joined");
            }

            // Step 13: SyncVar Received
            if (joined)
            {
                await RunStepWithWait("SyncVar Received", () =>
                {
                    var local = EOSNetworkPlayer.LocalPlayer;
                    if (local == null)
                        return (StepStatus.Running, "No local player yet");
                    if (!string.IsNullOrEmpty(local.Puid))
                        return (StepStatus.Pass, $"PUID: {Truncate(local.Puid, 12)}, Name: {local.DisplayName}");
                    return (StepStatus.Running, "Waiting for PUID sync...");
                });
            }
            else
            {
                AddSkipped("SyncVar Received", "Not joined");
            }

            // Step 14: Voice Connected
            if (joined)
            {
                await RunStepWithWait("Voice Connected", () => CheckVoiceConnected());
            }
            else
            {
                AddSkipped("Voice Connected", "Not joined");
            }

            // Step 15: Voice Players Wired
            if (joined)
            {
                await RunStepWithWait("Voice Players Wired", () => CheckVoicePlayersWired());
            }
            else
            {
                AddSkipped("Voice Players Wired", "Not joined");
            }

            // Step 16: Stability Hold
            if (joined)
            {
                await RunStepAsync("Stability Hold", async () =>
                {
                    long bytesBefore = _transport.TotalBytesSent + _transport.TotalBytesReceived;
                    await Task.Delay(Mathf.RoundToInt(_holdDuration * 1000));
                    long bytesAfter = _transport.TotalBytesSent + _transport.TotalBytesReceived;
                    long delta = bytesAfter - bytesBefore;

                    bool clientOk = _transport.GetConnectionState(false) == LocalConnectionState.Started;
                    if (!clientOk)
                        return (StepStatus.Fail, "Client connection dropped!");

                    return (StepStatus.Pass, $"Stable {_holdDuration}s, {delta} bytes");
                });
            }
            else
            {
                AddSkipped("Stability Hold", "Not joined");
            }

            // Stress mode: migration verification
            if (_testMode == TestMode.Stress && joined)
            {
                // Snapshot locals for SyncVar verification
                string snapshotPuid = null;
                string snapshotName = null;
                Color snapshotColor = Color.clear;

                // Step 15: Pre-Migration Snapshot
                await RunStep("Pre-Migration Snapshot", () =>
                {
                    var local = EOSNetworkPlayer.LocalPlayer;
                    if (local == null)
                        return (StepStatus.Fail, "No local player");

                    snapshotPuid = local.Puid;
                    snapshotName = local.DisplayName;

                    var renderer = local.GetComponent<Renderer>();
                    if (renderer != null && renderer.material != null)
                        snapshotColor = renderer.material.color;

                    bool hasPuid = !string.IsNullOrEmpty(snapshotPuid);
                    bool hasColor = snapshotColor != Color.clear;
                    string colorHex = hasColor ? $"#{ColorUtility.ToHtmlStringRGB(snapshotColor)}" : "none";

                    if (!hasPuid)
                        return (StepStatus.Fail, "PUID is empty");

                    return (StepStatus.Pass, $"PUID: {Truncate(snapshotPuid, 12)}, Color: {colorHex}");
                });

                // Step 16: Migration (detect start OR already completed)
                // IsMigrating is transient — may flip true→false within a single frame.
                // So we also accept "already became server" as proof migration happened.
                await RunStepWithWait("Migration", () =>
                {
                    var hm = HostMigrationManager.Instance;
                    if (hm == null)
                        return (StepStatus.Fail, "No HostMigrationManager");

                    // Still migrating — keep waiting for it to finish
                    if (hm.IsMigrating)
                        return (StepStatus.Running, "Migrating...");

                    // Migration already completed (or was so fast we missed the flag)
                    // — check if we became the server
                    var serverState = _transport.GetConnectionState(true);
                    if (serverState == LocalConnectionState.Started)
                        return (StepStatus.Pass, "Migrated — now hosting");

                    // Also check if we became lobby owner (migration happened at EOS level)
                    if (_transport.IsInLobby && _transport.IsLobbyOwner)
                        return (StepStatus.Pass, "Became lobby owner — migration complete");

                    return (StepStatus.Running, "Waiting for host to leave...");
                }, _migrationTimeout);

                // Step 17: SyncVars Survived
                await RunStep("SyncVars Survived", () =>
                {
                    var local = EOSNetworkPlayer.LocalPlayer;
                    if (local == null)
                        return (StepStatus.Fail, "No local player after migration");

                    var results = new List<string>();
                    bool allMatch = true;

                    // PUID
                    if (snapshotPuid != null)
                    {
                        if (local.Puid == snapshotPuid)
                            results.Add("PUID:OK");
                        else
                        {
                            results.Add($"PUID:MISMATCH({Truncate(local.Puid, 8)})");
                            allMatch = false;
                        }
                    }

                    // DisplayName
                    if (snapshotName != null)
                    {
                        if (local.DisplayName == snapshotName)
                            results.Add("Name:OK");
                        else
                        {
                            results.Add($"Name:MISMATCH({local.DisplayName})");
                            allMatch = false;
                        }
                    }

                    // Color
                    if (snapshotColor != Color.clear)
                    {
                        var renderer = local.GetComponent<Renderer>();
                        Color currentColor = (renderer != null && renderer.material != null)
                            ? renderer.material.color
                            : Color.clear;

                        bool colorMatch = Mathf.Abs(currentColor.r - snapshotColor.r) < 0.01f
                                       && Mathf.Abs(currentColor.g - snapshotColor.g) < 0.01f
                                       && Mathf.Abs(currentColor.b - snapshotColor.b) < 0.01f;

                        if (colorMatch)
                            results.Add("Color:OK");
                        else
                        {
                            results.Add($"Color:MISMATCH(#{ColorUtility.ToHtmlStringRGB(currentColor)})");
                            allMatch = false;
                        }
                    }

                    string detail = string.Join(", ", results);
                    return allMatch ? (StepStatus.Pass, detail) : (StepStatus.Fail, detail);
                });

                // Step 18: Objects Survived
                await RunStep("Objects Survived", () =>
                {
                    int count = EOSNetworkPlayer.PlayerCount;
                    if (count >= 1)
                        return (StepStatus.Pass, $"{count} player(s) survived migration");
                    return (StepStatus.Fail, "No players after migration");
                });

                // Step 19: Teardown
                await RunTeardownStep();
            }
            else if (joined)
            {
                // Step 15: Leave
                await RunStepAsync("Leave", async () =>
                {
                    try
                    {
                        await _transport.LeaveLobbyAsync();
                        await Task.Delay(500);
                        bool leftLobby = !_transport.IsInLobby;
                        bool clientStopped = _transport.GetConnectionState(false) == LocalConnectionState.Stopped;

                        if (leftLobby && clientStopped)
                            return (StepStatus.Pass, "Clean leave");

                        string issues = "";
                        if (!leftLobby) issues += "Still in lobby. ";
                        if (!clientStopped) issues += "Client still connected. ";
                        return (StepStatus.Fail, issues.Trim());
                    }
                    catch (Exception ex)
                    {
                        return (StepStatus.Fail, $"Exception: {ex.Message}");
                    }
                });
            }
            else
            {
                AddSkipped(_testMode == TestMode.Stress ? "Migration Started" : "Leave", "Not joined");
            }
        }

        #endregion

        #region Voice Check Helpers

        private (StepStatus, string) CheckVoiceConnected()
        {
            var voice = EOSVoiceManager.Instance;
            if (voice == null)
                return (StepStatus.Pass, "No EOSVoiceManager (optional)");
            if (!voice.IsVoiceEnabled)
                return (StepStatus.Pass, "Voice not enabled for this lobby");
            if (voice.IsConnected && voice.ParticipantCount >= 1)
                return (StepStatus.Pass, $"Connected, {voice.ParticipantCount} participant(s)");
            if (voice.IsConnected)
                return (StepStatus.Running, "Connected, waiting for participants...");
            return (StepStatus.Running, "Connecting to RTC...");
        }

        private (StepStatus, string) CheckVoicePlayersWired()
        {
            var players = EOSNetworkPlayer.AllPlayers;
            if (players.Count == 0)
                return (StepStatus.Running, "No players yet");

            int wired = 0;
            int total = players.Count;
            var issues = new List<string>();

            foreach (var player in players)
            {
                var vp = player.GetComponent<EOSVoicePlayer>();
                if (vp == null)
                {
                    issues.Add($"conn:{player.ConnectionId} missing EOSVoicePlayer");
                    continue;
                }

                string playerPuid = player.Puid;
                string voicePuid = vp.ParticipantPuid;

                if (string.IsNullOrEmpty(playerPuid))
                {
                    issues.Add($"conn:{player.ConnectionId} no PUID yet");
                    continue;
                }

                if (playerPuid == voicePuid)
                {
                    wired++;
                }
                else
                {
                    issues.Add($"conn:{player.ConnectionId} PUID mismatch");
                }
            }

            if (wired == total)
                return (StepStatus.Pass, $"{wired}/{total} players wired");

            if (issues.Count > 0)
                return (StepStatus.Running, string.Join(", ", issues));

            return (StepStatus.Running, $"{wired}/{total} wired...");
        }

        #endregion

        #region Shared Steps

        private async Task RunTeardownStep()
        {
            await RunStepAsync("Teardown", async () =>
            {
                try
                {
                    await _transport.LeaveLobbyAsync();
                    await Task.Delay(500);

                    bool serverStopped = _transport.GetConnectionState(true) == LocalConnectionState.Stopped;
                    bool clientStopped = _transport.GetConnectionState(false) == LocalConnectionState.Stopped;
                    bool leftLobby = !_transport.IsInLobby;

                    if (serverStopped && clientStopped && leftLobby)
                        return (StepStatus.Pass, "Clean shutdown");

                    string issues = "";
                    if (!serverStopped) issues += "Server still running. ";
                    if (!clientStopped) issues += "Client still running. ";
                    if (!leftLobby) issues += "Still in lobby. ";
                    return (StepStatus.Fail, issues.Trim());
                }
                catch (Exception ex)
                {
                    return (StepStatus.Fail, $"Exception: {ex.Message}");
                }
            });
        }

        #endregion

        #region Step Runners

        private async Task RunStep(string name, Func<(StepStatus status, string detail)> check)
        {
            var step = new Step { Name = name, Status = StepStatus.Running, Detail = "" };
            _steps.Add(step);

            var sw = Stopwatch.StartNew();
            try
            {
                var (status, detail) = check();
                if (status == StepStatus.Warning)
                {
                    step.Status = StepStatus.Pass;
                    step.Detail = detail;
                    _passCount++;
                }
                else
                {
                    step.Status = status;
                    step.Detail = detail;
                    CountStatus(status);
                }
            }
            catch (Exception ex)
            {
                step.Status = StepStatus.Fail;
                step.Detail = $"Exception: {ex.Message}";
                _failCount++;
            }
            sw.Stop();
            step.ElapsedMs = sw.ElapsedMilliseconds;

            await Task.Yield();
        }

        private async Task RunStepAsync(string name, Func<Task<(StepStatus status, string detail)>> check, float? timeoutOverride = null)
        {
            var step = new Step { Name = name, Status = StepStatus.Running, Detail = "" };
            _steps.Add(step);

            float timeout = timeoutOverride ?? _stepTimeout;
            var sw = Stopwatch.StartNew();
            try
            {
                var timeoutTask = Task.Delay(Mathf.RoundToInt(timeout * 1000));
                var checkTask = check();
                var completed = await Task.WhenAny(checkTask, timeoutTask);

                if (completed == timeoutTask)
                {
                    step.Status = StepStatus.Fail;
                    step.Detail = $"Timeout ({timeout}s)";
                    _failCount++;
                }
                else
                {
                    var (status, detail) = checkTask.Result;
                    step.Status = status;
                    step.Detail = detail;
                    CountStatus(status);
                }
            }
            catch (Exception ex)
            {
                step.Status = StepStatus.Fail;
                step.Detail = $"Exception: {ex.Message}";
                _failCount++;
            }
            sw.Stop();
            step.ElapsedMs = sw.ElapsedMilliseconds;
        }

        private Task RunStepWithWait(string name, Func<(StepStatus status, string detail)> check)
        {
            return RunStepWithWait(name, check, _stepTimeout);
        }

        private async Task RunStepWithWait(string name, Func<(StepStatus status, string detail)> check, float timeout)
        {
            var step = new Step { Name = name, Status = StepStatus.Running, Detail = "Waiting..." };
            _steps.Add(step);

            var sw = Stopwatch.StartNew();
            float deadline = Time.unscaledTime + timeout;

            try
            {
                while (Time.unscaledTime < deadline)
                {
                    var (status, detail) = check();
                    step.Detail = detail;

                    if (status == StepStatus.Pass || status == StepStatus.Fail)
                    {
                        step.Status = status;
                        CountStatus(status);
                        sw.Stop();
                        step.ElapsedMs = sw.ElapsedMilliseconds;
                        return;
                    }

                    await Task.Yield();
                }

                step.Status = StepStatus.Fail;
                step.Detail = $"Timeout ({timeout}s)";
                _failCount++;
            }
            catch (Exception ex)
            {
                step.Status = StepStatus.Fail;
                step.Detail = $"Exception: {ex.Message}";
                _failCount++;
            }
            sw.Stop();
            step.ElapsedMs = sw.ElapsedMilliseconds;
        }

        private void AddSkipped(string name, string reason)
        {
            _steps.Add(new Step { Name = name, Status = StepStatus.Skipped, Detail = reason, ElapsedMs = 0 });
            _skipCount++;
        }

        private void CountStatus(StepStatus status)
        {
            switch (status)
            {
                case StepStatus.Pass: _passCount++; break;
                case StepStatus.Fail: _failCount++; break;
                case StepStatus.Skipped: _skipCount++; break;
            }
        }

        #endregion

        #region OnGUI

        private void BuildStyles()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;

            _boxStyle = new GUIStyle(GUI.skin.box);
            var bgTex = new Texture2D(1, 1);
            bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.88f));
            bgTex.Apply();
            _boxStyle.normal.background = bgTex;
            _boxStyle.padding = new RectOffset(8, 8, 6, 6);

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft
            };
            _headerStyle.normal.textColor = Cyan;

            _rowStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                richText = true
            };
            _rowStyle.normal.textColor = Color.white;

            _detailStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleRight,
                richText = true
            };
            _detailStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold
            };

            _summaryStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                richText = true,
                fontStyle = FontStyle.Bold
            };

            _modeButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                fontStyle = FontStyle.Normal
            };

            _modeActiveStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };
            _modeActiveStyle.normal.textColor = Cyan;
        }

        private void OnGUI()
        {
            if (!_enableOnGUI || !_visible) return;

            BuildStyles();

            float rowHeight = 22f;
            float headerHeight = 26f;
            float buttonHeight = 28f;
            bool showModeSelector = !_isRunning;
            float modeRowHeight = showModeSelector ? 28f : 0f;
            float summaryHeight = _phase == TestPhase.Done ? 30f : 0f;

            // Fixed chrome height (everything except the scrollable steps list)
            float fixedHeight = headerHeight + modeRowHeight + buttonHeight + 12f + summaryHeight;

            float stepsContentHeight = _steps.Count * rowHeight;
            float totalContentHeight = fixedHeight + stepsContentHeight + 20f;
            float maxHeight = Screen.height * 0.85f;
            float panelHeight = Mathf.Min(totalContentHeight, maxHeight);

            // How much space the scroll area gets
            float scrollAreaHeight = panelHeight - fixedHeight - 16f;

            float panelX = Screen.width - _panelWidth - 10;
            var panelRect = new Rect(panelX, 10, _panelWidth, panelHeight);

            GUI.Box(panelRect, GUIContent.none, _boxStyle);

            GUILayout.BeginArea(new Rect(panelRect.x + 8, panelRect.y + 4, panelRect.width - 16, panelRect.height - 8));

            // Header with mode/role info
            GUILayout.BeginHorizontal();
            string headerLabel = _testMode switch
            {
                TestMode.Solo => "EOS Health Check [Solo]",
                TestMode.Duo => _duoRole != DuoRole.Unknown ? $"EOS Health Check [Duo:{_duoRole}]" : "EOS Health Check [Duo]",
                TestMode.Stress => _duoRole != DuoRole.Unknown ? $"EOS Health Check [Stress:{_duoRole}]" : "EOS Health Check [Stress]",
                _ => "EOS Health Check"
            };
            GUILayout.Label(headerLabel, _headerStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<color=#{ColorUtility.ToHtmlStringRGB(Gray)}>[{_toggleKey}]</color>", _detailStyle);
            GUILayout.EndHorizontal();

            // Mode selector (when not running)
            if (showModeSelector)
            {
                GUILayout.Space(2);
                GUILayout.BeginHorizontal();
                foreach (TestMode mode in Enum.GetValues(typeof(TestMode)))
                {
                    var style = _testMode == mode ? _modeActiveStyle : _modeButtonStyle;
                    if (GUILayout.Button(mode.ToString(), style, GUILayout.Height(22f)))
                    {
                        _testMode = mode;
                    }
                }
                GUILayout.EndHorizontal();
            }

            // Run button
            GUILayout.Space(2);
            GUI.enabled = !_isRunning;
            string runLabel = _isRunning ? "Running..." : (_phase == TestPhase.Done ? $"Re-Run {_testMode}" : $"Run {_testMode}");
            if (GUILayout.Button(runLabel, _buttonStyle, GUILayout.Height(buttonHeight)))
            {
                RunHealthCheck();
            }
            GUI.enabled = true;

            GUILayout.Space(4);

            // Steps list — always in a scroll view with explicit height
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(scrollAreaHeight));

            foreach (var step in _steps)
            {
                GUILayout.BeginHorizontal();

                string dot = StatusDot(step.Status);
                string statusLabel = step.Status == StepStatus.Running ? " ..." : "";
                GUILayout.Label($"{dot} {step.Name}{statusLabel}", _rowStyle, GUILayout.Width(_panelWidth * 0.45f));

                GUILayout.FlexibleSpace();
                string detailColor = StatusHexColor(step.Status);
                string timing = step.ElapsedMs > 0 ? $" <color=#{ColorUtility.ToHtmlStringRGB(Gray)}>({step.ElapsedMs}ms)</color>" : "";
                GUILayout.Label($"<color={detailColor}>{step.Detail}</color>{timing}", _detailStyle);

                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();

            // Summary
            if (_phase == TestPhase.Done)
            {
                GUILayout.Space(4);
                var sepRect = GUILayoutUtility.GetRect(GUIContent.none, GUI.skin.box, GUILayout.Height(1));
                GUI.DrawTexture(sepRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0.3f, 0.3f, 0.3f), 0f, 0f);
                GUILayout.Space(2);

                string passHex = ColorUtility.ToHtmlStringRGB(Green);
                string failHex = ColorUtility.ToHtmlStringRGB(Red);
                string grayHex = ColorUtility.ToHtmlStringRGB(Gray);

                string result = _failCount == 0 ? $"<color=#{passHex}>ALL PASS</color>" : $"<color=#{failHex}>{_failCount} FAILED</color>";
                GUILayout.Label($"{result}  |  <color=#{passHex}>{_passCount}</color> pass  <color=#{failHex}>{_failCount}</color> fail  <color=#{grayHex}>{_skipCount}</color> skip  |  {_totalElapsed:F1}s", _summaryStyle);
            }

            GUILayout.EndArea();
        }

        #endregion

        #region Helpers

        private static string StatusDot(StepStatus status)
        {
            string hex = status switch
            {
                StepStatus.Pass    => ColorUtility.ToHtmlStringRGB(Green),
                StepStatus.Warning => ColorUtility.ToHtmlStringRGB(Yellow),
                StepStatus.Running => ColorUtility.ToHtmlStringRGB(Yellow),
                StepStatus.Fail    => ColorUtility.ToHtmlStringRGB(Red),
                StepStatus.Skipped => ColorUtility.ToHtmlStringRGB(Gray),
                _                  => ColorUtility.ToHtmlStringRGB(Gray)
            };
            return $"<color=#{hex}>\u25CF</color>";
        }

        private static string StatusHexColor(StepStatus status)
        {
            string hex = status switch
            {
                StepStatus.Pass    => ColorUtility.ToHtmlStringRGB(Green),
                StepStatus.Warning => ColorUtility.ToHtmlStringRGB(Yellow),
                StepStatus.Running => ColorUtility.ToHtmlStringRGB(Yellow),
                StepStatus.Fail    => ColorUtility.ToHtmlStringRGB(Red),
                StepStatus.Skipped => ColorUtility.ToHtmlStringRGB(Gray),
                _                  => ColorUtility.ToHtmlStringRGB(Gray)
            };
            return $"#{hex}";
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            return s.Length <= maxLen ? s : s[..maxLen] + "...";
        }

        #endregion
    }
}
