using System.Collections.Generic;
using System.Linq;
using EOSNative;
using EOSNative.Voice;
using FishNet.Managing;
using FishNet.Transport.EOSNative.Migration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FishNet.Transport.EOSNative.Diagnostics
{
    /// <summary>
    /// Runtime diagnostic overlay for the EOS networking stack.
    /// Drop on any GameObject. Toggle visibility with F12.
    /// Uses OnGUI — no Canvas or prefab setup needed.
    /// </summary>
    public class EOSNetworkDiagnostics : MonoBehaviour
    {
        #region Types

        public enum DiagStatus { Pass, Warning, Fail, Skipped }

        public struct DiagCheck
        {
            public string Name;
            public DiagStatus Status;
            public string Detail;
        }

        #endregion

        #region Settings

        [Header("Display")]
        [Tooltip("Keyboard shortcut to toggle the panel.")]
        [SerializeField] private Key _toggleKey = Key.F12;

        [Tooltip("Panel width in pixels.")]
        [SerializeField] private float _panelWidth = 370f;

        [Tooltip("How often checks refresh (seconds).")]
        [SerializeField] private float _refreshInterval = 1f;

        #endregion

        #region State

        private bool _visible = true;
        private readonly List<DiagCheck> _checks = new();
        private float _nextRefresh;

        // Cached refs — found once, re-found if null
        private NetworkManager _networkManager;
        private EOSNativeTransport _transport;
        private HostMigrationPlayerSpawner _playerSpawner;

        // Bandwidth tracking
        private long _prevBytesSent;
        private long _prevBytesReceived;
        private float _lastTrafficTime;
        private long _deltaSent;
        private long _deltaReceived;

        // Role string
        private string _roleLabel = "OFFLINE";
        private string _lobbyLabel = "";

        // GUI
        private Vector2 _scrollPos;
        private GUIStyle _boxStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _rowStyle;
        private GUIStyle _detailStyle;
        private bool _stylesBuilt;

        // Status colors
        private static readonly Color Green  = new(0.2f, 0.9f, 0.2f);
        private static readonly Color Yellow = new(1f, 0.85f, 0.1f);
        private static readonly Color Red    = new(1f, 0.25f, 0.2f);
        private static readonly Color Gray   = new(0.5f, 0.5f, 0.5f);

        #endregion

        #region Lifecycle

        private void Start()
        {
            CacheRefs();
            RunChecks();
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current[_toggleKey].wasPressedThisFrame)
                _visible = !_visible;

            if (!_visible) return;

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + _refreshInterval;
                CacheRefs();
                RunChecks();
            }
        }

        #endregion

        #region Ref Caching

        private void CacheRefs()
        {
            if (_networkManager == null)
                _networkManager = FindAnyObjectByType<NetworkManager>();

            if (_transport == null && _networkManager != null)
                _transport = _networkManager.TransportManager?.Transport as EOSNativeTransport;

            // Fallback: find transport directly if NM wiring is odd
            if (_transport == null)
                _transport = FindAnyObjectByType<EOSNativeTransport>();

            if (_playerSpawner == null)
                _playerSpawner = FindAnyObjectByType<HostMigrationPlayerSpawner>();
        }

        #endregion

        #region Checks

        private void RunChecks()
        {
            _checks.Clear();

            // Gather state booleans once
            var eosInstance = EOSManager.Instance;
            bool sdkInit = eosInstance != null && eosInstance.IsInitialized;
            bool loggedIn = sdkInit && eosInstance.IsLoggedIn;
            bool hasNM = _networkManager != null;
            bool hasTransport = _transport != null;

            bool isServer = hasTransport && _transport.GetConnectionState(true) == FishNet.Transporting.LocalConnectionState.Started;
            bool isClient = hasTransport && _transport.GetConnectionState(false) == FishNet.Transporting.LocalConnectionState.Started;
            bool inLobby = hasTransport && _transport.IsInLobby;
            bool isOffline = hasTransport && _transport.IsOfflineMode;

            // Build role label
            if (isOffline)
                _roleLabel = "OFFLINE";
            else if (isServer && isClient)
                _roleLabel = "HOST";
            else if (isServer)
                _roleLabel = "SERVER";
            else if (isClient)
                _roleLabel = "CLIENT";
            else
                _roleLabel = "IDLE";

            // Lobby label
            if (inLobby)
            {
                var lobby = _transport.CurrentLobby;
                _lobbyLabel = $"{lobby.JoinCode ?? "?"} ({lobby.MemberCount}/{lobby.MaxMembers})";
            }
            else
            {
                _lobbyLabel = "";
            }

            // === Always-On Checks ===

            // 1. EOS SDK Initialized
            _checks.Add(new DiagCheck
            {
                Name = "EOS Initialized",
                Status = sdkInit ? DiagStatus.Pass : DiagStatus.Fail,
                Detail = sdkInit ? "OK" : "SDK not initialized"
            });

            // 2. EOS Logged In
            if (sdkInit)
            {
                string puidStr = loggedIn ? Truncate(eosInstance.LocalProductUserId?.ToString(), 12) : "";
                _checks.Add(new DiagCheck
                {
                    Name = "Logged In",
                    Status = loggedIn ? DiagStatus.Pass : DiagStatus.Fail,
                    Detail = loggedIn ? puidStr : "Not logged in"
                });
            }
            else
            {
                _checks.Add(new DiagCheck { Name = "Logged In", Status = DiagStatus.Skipped, Detail = "SDK not init" });
            }

            // 3. NetworkManager Found
            _checks.Add(new DiagCheck
            {
                Name = "NetworkManager",
                Status = hasNM ? DiagStatus.Pass : DiagStatus.Fail,
                Detail = hasNM ? "OK" : "Not found"
            });

            // 4. Transport Wired
            if (hasNM)
            {
                _checks.Add(new DiagCheck
                {
                    Name = "Transport Wired",
                    Status = hasTransport ? DiagStatus.Pass : DiagStatus.Fail,
                    Detail = hasTransport ? "OK" : "Wrong/null transport"
                });
            }
            else
            {
                _checks.Add(new DiagCheck { Name = "Transport Wired", Status = DiagStatus.Skipped, Detail = "No NM" });
            }

            // === Lobby Checks ===

            if (!isOffline && hasTransport)
            {
                // 5. Lobby Joined
                _checks.Add(new DiagCheck
                {
                    Name = "Lobby Joined",
                    Status = inLobby ? DiagStatus.Pass : DiagStatus.Skipped,
                    Detail = inLobby ? _lobbyLabel : "Not in lobby"
                });

                // 6. Owner PUID
                if (inLobby)
                {
                    var ownerPuid = _transport.CurrentLobby.OwnerPuid;
                    bool hasOwner = !string.IsNullOrEmpty(ownerPuid);
                    _checks.Add(new DiagCheck
                    {
                        Name = "Owner PUID",
                        Status = hasOwner ? DiagStatus.Pass : DiagStatus.Warning,
                        Detail = hasOwner ? Truncate(ownerPuid, 12) : "Missing (migration?)"
                    });
                }
            }

            // === Server Checks ===

            if (isServer && !isOffline)
            {
                // 7. Server Running
                _checks.Add(new DiagCheck
                {
                    Name = "Server Running",
                    Status = DiagStatus.Pass,
                    Detail = $"{_transport.ServerP2PConnectionCount} client(s)"
                });

                // 8. Connections Healthy
                var connInfo = _transport.GetServerConnectionInfo();
                if (connInfo.Count > 0)
                {
                    float maxAge = connInfo.Max(c => c.lastPacketAge);
                    bool healthy = maxAge < 5f;
                    _checks.Add(new DiagCheck
                    {
                        Name = "Connections Healthy",
                        Status = healthy ? DiagStatus.Pass : DiagStatus.Warning,
                        Detail = healthy ? $"all < {maxAge:F1}s" : $"stale: {maxAge:F1}s"
                    });
                }
            }

            // === Client Checks ===

            if (!isServer || isClient)
            {
                // 9. Client Connected
                if (hasTransport)
                {
                    var clientState = _transport.GetConnectionState(false);
                    if (clientState == FishNet.Transporting.LocalConnectionState.Started)
                    {
                        _checks.Add(new DiagCheck { Name = "Client Connected", Status = DiagStatus.Pass, Detail = "OK" });
                    }
                    else if (clientState == FishNet.Transporting.LocalConnectionState.Starting)
                    {
                        _checks.Add(new DiagCheck { Name = "Client Connected", Status = DiagStatus.Warning, Detail = "Connecting..." });
                    }
                    else if (inLobby || !string.IsNullOrEmpty(_transport.RemoteProductUserId))
                    {
                        // Expected to be connected but isn't
                        _checks.Add(new DiagCheck { Name = "Client Connected", Status = DiagStatus.Fail, Detail = "Not connected" });
                    }
                    else
                    {
                        _checks.Add(new DiagCheck { Name = "Client Connected", Status = DiagStatus.Skipped, Detail = "Idle" });
                    }
                }

                // 10. Remote Host Set
                if (hasTransport && isClient && !isServer)
                {
                    bool hasRemote = !string.IsNullOrEmpty(_transport.RemoteProductUserId);
                    _checks.Add(new DiagCheck
                    {
                        Name = "Remote Host Set",
                        Status = hasRemote ? DiagStatus.Pass : DiagStatus.Fail,
                        Detail = hasRemote ? Truncate(_transport.RemoteProductUserId, 12) : "No host PUID"
                    });
                }
            }

            // === Spawning Checks ===

            // 11. Player Spawner Ready
            {
                bool hasSpawner = _playerSpawner != null;
                bool hasPrefab = hasSpawner && _playerSpawner.PlayerPrefab != null;
                if (hasSpawner && hasPrefab)
                    _checks.Add(new DiagCheck { Name = "Player Spawner", Status = DiagStatus.Pass, Detail = "Prefab OK" });
                else if (hasSpawner)
                    _checks.Add(new DiagCheck { Name = "Player Spawner", Status = DiagStatus.Warning, Detail = "No prefab" });
                else
                    _checks.Add(new DiagCheck { Name = "Player Spawner", Status = DiagStatus.Warning, Detail = "Not found" });
            }

            // 12. Local Player Exists
            if (isClient)
            {
                var localPlayer = EOSNetworkPlayer.LocalPlayer;
                if (localPlayer != null)
                {
                    _checks.Add(new DiagCheck
                    {
                        Name = "Local Player",
                        Status = DiagStatus.Pass,
                        Detail = $"Conn: {localPlayer.ConnectionId}"
                    });
                }
                else
                {
                    _checks.Add(new DiagCheck
                    {
                        Name = "Local Player",
                        Status = DiagStatus.Warning,
                        Detail = "Not spawned yet"
                    });
                }
            }

            // === Voice Check ===

            if (inLobby)
            {
                // 13. Voice Connected
                var voice = EOSVoiceManager.Instance;
                if (voice != null && voice.IsConnected)
                {
                    _checks.Add(new DiagCheck
                    {
                        Name = "Voice Connected",
                        Status = DiagStatus.Pass,
                        Detail = $"{voice.ParticipantCount} user(s)"
                    });
                }
                else
                {
                    _checks.Add(new DiagCheck
                    {
                        Name = "Voice Connected",
                        Status = DiagStatus.Warning,
                        Detail = "Not connected"
                    });
                }
            }

            // === Host Migration Check ===

            {
                var migration = HostMigrationManager.Instance;
                if (migration != null && migration.IsMigrating)
                {
                    _checks.Add(new DiagCheck
                    {
                        Name = "Host Migration",
                        Status = DiagStatus.Warning,
                        Detail = $"In progress ({migration.TotalTrackedCount} objects)"
                    });
                }
            }

            // === Bandwidth Check ===

            if ((isServer || isClient) && hasTransport)
            {
                // 14. Traffic Flowing
                long sent = _transport.TotalBytesSent;
                long recv = _transport.TotalBytesReceived;
                _deltaSent = sent - _prevBytesSent;
                _deltaReceived = recv - _prevBytesReceived;

                bool trafficFlowing = _deltaSent > 0 || _deltaReceived > 0;
                if (trafficFlowing)
                    _lastTrafficTime = Time.unscaledTime;

                float silenceDuration = Time.unscaledTime - _lastTrafficTime;

                _prevBytesSent = sent;
                _prevBytesReceived = recv;

                string upStr = FormatBytes(_deltaSent);
                string downStr = FormatBytes(_deltaReceived);

                if (silenceDuration > 5f)
                {
                    _checks.Add(new DiagCheck
                    {
                        Name = "Traffic",
                        Status = DiagStatus.Warning,
                        Detail = $"Silent {silenceDuration:F0}s"
                    });
                }
                else
                {
                    _checks.Add(new DiagCheck
                    {
                        Name = "Traffic",
                        Status = DiagStatus.Pass,
                        Detail = $"\u2191{upStr} \u2193{downStr}/s"
                    });
                }
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
            bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.85f));
            bgTex.Apply();
            _boxStyle.normal.background = bgTex;
            _boxStyle.padding = new RectOffset(8, 8, 6, 6);

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft
            };
            _headerStyle.normal.textColor = Color.white;

            _rowStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                richText = true
            };
            _rowStyle.normal.textColor = Color.white;

            _detailStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleRight,
                richText = true
            };
            _detailStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        }

        private void OnGUI()
        {
            if (!_visible) return;

            BuildStyles();

            float rowHeight = 20f;
            float headerHeight = 24f;
            float subHeaderHeight = 20f;
            float padding = 4f;
            float totalHeight = headerHeight + subHeaderHeight + padding + (_checks.Count * rowHeight) + 16f;
            float maxHeight = Screen.height * 0.8f;
            bool needsScroll = totalHeight > maxHeight;
            float panelHeight = needsScroll ? maxHeight : totalHeight;

            var panelRect = new Rect(10, 10, _panelWidth, panelHeight);

            GUI.Box(panelRect, GUIContent.none, _boxStyle);

            GUILayout.BeginArea(new Rect(panelRect.x + 8, panelRect.y + 4, panelRect.width - 16, panelRect.height - 8));

            // Header
            GUILayout.BeginHorizontal();
            GUILayout.Label("EOS Network Diagnostics", _headerStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"[{_toggleKey}]", _detailStyle);
            GUILayout.EndHorizontal();

            // Sub-header: Role + Lobby
            string subHeader = $"Role: {_roleLabel}";
            if (!string.IsNullOrEmpty(_lobbyLabel))
                subHeader += $"  |  Lobby: {_lobbyLabel}";
            GUILayout.Label(subHeader, _rowStyle);

            // Separator
            var separatorRect = GUILayoutUtility.GetRect(GUIContent.none, GUI.skin.box, GUILayout.Height(1));
            GUI.DrawTexture(separatorRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0.3f, 0.3f, 0.3f), 0f, 0f);

            GUILayout.Space(2);

            // Checks list
            if (needsScroll)
                _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            foreach (var check in _checks)
            {
                GUILayout.BeginHorizontal();

                // Color dot + name
                string dot = StatusDot(check.Status);
                GUILayout.Label($"{dot} {check.Name}", _rowStyle, GUILayout.Width(_panelWidth * 0.55f));

                // Detail right-aligned
                GUILayout.FlexibleSpace();
                string detailColor = StatusHexColor(check.Status);
                GUILayout.Label($"<color={detailColor}>{check.Detail}</color>", _detailStyle);

                GUILayout.EndHorizontal();
            }

            if (needsScroll)
                GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        #endregion

        #region Helpers

        private static string StatusDot(DiagStatus status)
        {
            string hex = status switch
            {
                DiagStatus.Pass    => ColorUtility.ToHtmlStringRGB(Green),
                DiagStatus.Warning => ColorUtility.ToHtmlStringRGB(Yellow),
                DiagStatus.Fail    => ColorUtility.ToHtmlStringRGB(Red),
                _                  => ColorUtility.ToHtmlStringRGB(Gray)
            };
            return $"<color=#{hex}>\u25CF</color>";
        }

        private static string StatusHexColor(DiagStatus status)
        {
            string hex = status switch
            {
                DiagStatus.Pass    => ColorUtility.ToHtmlStringRGB(Green),
                DiagStatus.Warning => ColorUtility.ToHtmlStringRGB(Yellow),
                DiagStatus.Fail    => ColorUtility.ToHtmlStringRGB(Red),
                _                  => ColorUtility.ToHtmlStringRGB(Gray)
            };
            return $"#{hex}";
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            return s.Length <= maxLen ? s : s[..maxLen] + "...";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1}K";
            return $"{bytes / (1024f * 1024f):F1}M";
        }

        #endregion
    }
}
