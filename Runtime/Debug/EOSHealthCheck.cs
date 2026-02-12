using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using EOSNative;
using EOSNative.Lobbies;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transport.EOSNative.Migration;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FishNet.Transport.EOSNative.Diagnostics
{
    /// <summary>
    /// Automated runtime health check for the EOS + FishNet stack.
    /// Auto-attaches to the EOSNativeTransport or EOSManager GameObject at runtime.
    /// No manual setup needed — just have the package imported.
    /// Press F11 to toggle the panel, press "Run" to execute a full self-test.
    /// Uses OnGUI — no Canvas or prefab setup needed.
    /// </summary>
    public class EOSHealthCheck : MonoBehaviour
    {
        #region Auto-Create

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            // Don't double-create
            if (FindAnyObjectByType<EOSHealthCheck>() != null)
                return;

            // Attach to transport GameObject if available
            var transport = FindAnyObjectByType<EOSNativeTransport>();
            if (transport != null)
            {
                transport.gameObject.AddComponent<EOSHealthCheck>();
                return;
            }

            // Fallback: attach to EOSManager
            var eosManager = FindAnyObjectByType<EOSManager>();
            if (eosManager != null)
            {
                eosManager.gameObject.AddComponent<EOSHealthCheck>();
                return;
            }

            // Neither found — skip silently, nothing to health-check
        }

        #endregion

        #region Types

        public enum StepStatus { Pending, Running, Pass, Warning, Fail, Skipped }

        public class Step
        {
            public string Name;
            public StepStatus Status;
            public string Detail;
            public long ElapsedMs;
        }

        private enum TestPhase { Idle, Running, Done }

        #endregion

        #region Settings

        [Header("Display")]
        [Tooltip("Keyboard shortcut to toggle the panel.")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.F11;

        [Tooltip("Panel width in pixels.")]
        [SerializeField] private float _panelWidth = 400f;

        [Header("Test Settings")]
        [Tooltip("Timeout per step in seconds.")]
        [SerializeField] private float _stepTimeout = 10f;

        [Tooltip("How long to hold the connected state before teardown (seconds).")]
        [SerializeField] private float _holdDuration = 2f;

        [Tooltip("Auto-run health check on Start (after auto-init delay).")]
        [SerializeField] private bool _autoRunOnStart = false;

        [Tooltip("Seconds to wait before auto-running (lets auto-init finish).")]
        [SerializeField] private float _autoRunDelay = 3f;

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
            if (Input.GetKeyDown(_toggleKey))
                _visible = !_visible;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Starts the automated health check sequence.
        /// </summary>
        public void RunHealthCheck()
        {
            if (_isRunning) return;
            _ = RunHealthCheckAsync();
        }

        #endregion

        #region Test Sequence

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

            // --- Step 1: Check scene setup ---
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

            // --- Step 2: EOS Manager ---
            await RunStep("EOSManager", () =>
            {
                if (EOSManager.Instance == null)
                    return (StepStatus.Fail, "EOSManager.Instance is null");
                return (StepStatus.Pass, "Instance exists");
            });

            // --- Step 3: EOS SDK Initialized ---
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

            // --- Step 4: EOS Logged In ---
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

            // --- Step 5: Lobby Manager ---
            await RunStep("LobbyManager", () =>
            {
                if (_transport == null)
                    return (StepStatus.Skipped, "No transport");
                var lm = _transport.LobbyManager;
                if (lm == null)
                    return (StepStatus.Fail, "LobbyManager is null");
                return (StepStatus.Pass, "Instance OK");
            });

            // --- Step 6: P2P Interface ---
            await RunStep("P2P Interface", () =>
            {
                if (!loggedIn)
                    return (StepStatus.Skipped, "Not logged in");
                var p2p = EOSManager.Instance.P2PInterface;
                if (p2p == null)
                    return (StepStatus.Fail, "P2PInterface is null");
                return (StepStatus.Pass, "Available");
            });

            // --- Step 7: Host Migration Manager ---
            await RunStep("HostMigrationManager", () =>
            {
                var hm = HostMigrationManager.Instance;
                if (hm == null)
                    return (StepStatus.Warning, "Not found (optional)");
                return (StepStatus.Pass, "Instance OK");
            });

            // --- Step 8: Player Spawner ---
            await RunStep("PlayerSpawner", () =>
            {
                var spawner = FindAnyObjectByType<HostMigrationPlayerSpawner>();
                if (spawner == null)
                    return (StepStatus.Warning, "Not found (optional)");
                if (spawner.PlayerPrefab == null)
                    return (StepStatus.Warning, "No prefab assigned");
                return (StepStatus.Pass, $"Prefab: {spawner.PlayerPrefab.name}");
            });

            // --- Step 9: Create Lobby (Host Test) ---
            bool lobbyCreated = false;
            string testJoinCode = null;
            if (loggedIn && _transport != null)
            {
                await RunStepAsync("Host Lobby", async () =>
                {
                    // Don't test if already in a lobby
                    if (_transport.IsInLobby)
                        return (StepStatus.Skipped, $"Already in lobby: {_transport.CurrentLobby.JoinCode}");

                    var (result, lobby) = await _transport.HostLobbyAsync($"HC{UnityEngine.Random.Range(1000, 9999)}");
                    if (result == Epic.OnlineServices.Result.Success)
                    {
                        lobbyCreated = true;
                        testJoinCode = lobby.JoinCode;
                        return (StepStatus.Pass, $"Code: {lobby.JoinCode}");
                    }
                    return (StepStatus.Fail, $"Result: {result}");
                });
            }
            else
            {
                AddSkipped("Host Lobby", "Not logged in");
            }

            // --- Step 10: Verify Server Started ---
            if (lobbyCreated)
            {
                await RunStepWithWait("Server Started", () =>
                {
                    if (_transport == null) return (StepStatus.Fail, "No transport");
                    var state = _transport.GetConnectionState(true);
                    if (state == LocalConnectionState.Started)
                        return (StepStatus.Pass, "Running");
                    if (state == LocalConnectionState.Starting)
                        return (StepStatus.Running, "Starting...");
                    return (StepStatus.Fail, $"State: {state}");
                });
            }
            else
            {
                AddSkipped("Server Started", "No lobby");
            }

            // --- Step 11: Verify Client (Host) Started ---
            if (lobbyCreated)
            {
                await RunStepWithWait("Client (Host) Started", () =>
                {
                    if (_transport == null) return (StepStatus.Fail, "No transport");
                    var state = _transport.GetConnectionState(false);
                    if (state == LocalConnectionState.Started)
                    {
                        bool hasClientHost = _transport.HasClientHost;
                        return (StepStatus.Pass, hasClientHost ? "ClientHost active" : "Connected");
                    }
                    if (state == LocalConnectionState.Starting)
                        return (StepStatus.Running, "Connecting...");
                    return (StepStatus.Fail, $"State: {state}");
                });
            }
            else
            {
                AddSkipped("Client (Host) Started", "No lobby");
            }

            // --- Step 12: Verify Lobby State ---
            if (lobbyCreated)
            {
                await RunStep("Lobby State", () =>
                {
                    if (!_transport.IsInLobby)
                        return (StepStatus.Fail, "Not in lobby");
                    if (!_transport.IsLobbyOwner)
                        return (StepStatus.Fail, "Not owner");
                    var lobby = _transport.CurrentLobby;
                    return (StepStatus.Pass, $"Owner, {lobby.MemberCount}/{lobby.MaxMembers} members");
                });
            }
            else
            {
                AddSkipped("Lobby State", "No lobby");
            }

            // --- Step 13: Hold & verify traffic ---
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
                    return (StepStatus.Pass, $"Stable {_holdDuration}s (no traffic yet — normal for solo host)");
                });
            }
            else
            {
                AddSkipped("Connection Hold", "No lobby");
            }

            // --- Step 14: Teardown ---
            if (lobbyCreated)
            {
                await RunStepAsync("Teardown", async () =>
                {
                    try
                    {
                        await _transport.LeaveLobbyAsync();

                        // Wait briefly for state to settle
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
            else
            {
                AddSkipped("Teardown", "Nothing to tear down");
            }

            // Finalize
            totalSw.Stop();
            _totalElapsed = totalSw.ElapsedMilliseconds / 1000f;
            _phase = TestPhase.Done;
            _isRunning = false;

            // Log summary
            string summary = $"[EOSHealthCheck] Done: {_passCount} pass, {_failCount} fail, {_skipCount} skip in {_totalElapsed:F1}s";
            if (_failCount > 0)
                Debug.LogWarning(summary);
            else
                Debug.Log(summary);
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
                // Treat Warning as a pass for counting
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

            // Yield a frame so GUI updates
            await Task.Yield();
        }

        private async Task RunStepAsync(string name, Func<Task<(StepStatus status, string detail)>> check)
        {
            var step = new Step { Name = name, Status = StepStatus.Running, Detail = "" };
            _steps.Add(step);

            var sw = Stopwatch.StartNew();
            try
            {
                var timeoutTask = Task.Delay(Mathf.RoundToInt(_stepTimeout * 1000));
                var checkTask = check();
                var completed = await Task.WhenAny(checkTask, timeoutTask);

                if (completed == timeoutTask)
                {
                    step.Status = StepStatus.Fail;
                    step.Detail = $"Timeout ({_stepTimeout}s)";
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

        private async Task RunStepWithWait(string name, Func<(StepStatus status, string detail)> check)
        {
            var step = new Step { Name = name, Status = StepStatus.Running, Detail = "Waiting..." };
            _steps.Add(step);

            var sw = Stopwatch.StartNew();
            float deadline = Time.unscaledTime + _stepTimeout;

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

                    // Still running — wait a frame
                    await Task.Yield();
                }

                // Timed out
                step.Status = StepStatus.Fail;
                step.Detail = $"Timeout ({_stepTimeout}s)";
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
        }

        private void OnGUI()
        {
            if (!_visible) return;

            BuildStyles();

            float rowHeight = 22f;
            float headerHeight = 26f;
            float buttonHeight = 28f;
            float summaryHeight = _phase == TestPhase.Done ? 24f : 0f;
            float contentHeight = headerHeight + buttonHeight + 8f + (_steps.Count * rowHeight) + summaryHeight + 20f;
            float maxHeight = Screen.height * 0.85f;
            bool needsScroll = contentHeight > maxHeight;
            float panelHeight = needsScroll ? maxHeight : contentHeight;

            // Position at top-right
            float panelX = Screen.width - _panelWidth - 10;
            var panelRect = new Rect(panelX, 10, _panelWidth, panelHeight);

            GUI.Box(panelRect, GUIContent.none, _boxStyle);

            GUILayout.BeginArea(new Rect(panelRect.x + 8, panelRect.y + 4, panelRect.width - 16, panelRect.height - 8));

            // Header
            GUILayout.BeginHorizontal();
            GUILayout.Label("EOS Health Check", _headerStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<color=#{ColorUtility.ToHtmlStringRGB(Gray)}>[{_toggleKey}]</color>", _detailStyle);
            GUILayout.EndHorizontal();

            // Run button
            GUILayout.Space(2);
            GUI.enabled = !_isRunning;
            if (GUILayout.Button(_isRunning ? "Running..." : (_phase == TestPhase.Done ? "Re-Run Health Check" : "Run Health Check"), _buttonStyle, GUILayout.Height(buttonHeight)))
            {
                RunHealthCheck();
            }
            GUI.enabled = true;

            GUILayout.Space(4);

            // Steps list
            if (needsScroll)
                _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            foreach (var step in _steps)
            {
                GUILayout.BeginHorizontal();

                // Status indicator + name
                string dot = StatusDot(step.Status);
                string statusLabel = step.Status == StepStatus.Running ? " ..." : "";
                GUILayout.Label($"{dot} {step.Name}{statusLabel}", _rowStyle, GUILayout.Width(_panelWidth * 0.45f));

                // Detail + timing
                GUILayout.FlexibleSpace();
                string detailColor = StatusHexColor(step.Status);
                string timing = step.ElapsedMs > 0 ? $" <color=#{ColorUtility.ToHtmlStringRGB(Gray)}>({step.ElapsedMs}ms)</color>" : "";
                GUILayout.Label($"<color={detailColor}>{step.Detail}</color>{timing}", _detailStyle);

                GUILayout.EndHorizontal();
            }

            if (needsScroll)
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
