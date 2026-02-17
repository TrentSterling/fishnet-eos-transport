using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FishNet;
using FishNet.Managing.Object;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using EOSNative;
using EOSNative.Logging;
using EOSNative.Lobbies;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FishNet.Transport.EOSNative.Migration
{
    /// <summary>
    /// Manages host migration for FishNet over EOS.
    /// Automatically tracks ALL NetworkObjects and handles state save/restore during migration.
    /// Add DoNotMigrate component to exclude specific objects.
    /// Add HostMigratable component for advanced SyncVar restoration (optional).
    /// </summary>
    public class HostMigrationManager : MonoBehaviour
    {
        #region Singleton

        private static HostMigrationManager _instance;
        public static HostMigrationManager Instance => _instance;

        #endregion

        #region Events

        /// <summary>
        /// Fired when migration starts (old host left, we're promoted).
        /// </summary>
        public event Action OnMigrationStarted;

        /// <summary>
        /// Fired when migration completes (server restarted, objects restored).
        /// Preserved for backward compatibility — fires for both success and failure.
        /// Prefer <see cref="OnMigrationFinished"/> for detailed results.
        /// </summary>
        public event Action OnMigrationCompleted;

        /// <summary>
        /// Fired when migration finishes with detailed result information.
        /// Fires for both success and failure — check <see cref="MigrationFinishedArgs.Success"/>.
        /// </summary>
        public event Action<MigrationFinishedArgs> OnMigrationFinished;

        #endregion

        #region Serialized Fields

        [Header("Prefab Collection")]
        [Tooltip("PrefabObjects collection containing all migratable prefabs.")]
        [SerializeField]
        private PrefabObjects _prefabCollection;

        [Header("Migration Settings")]
        [Tooltip("Maximum time in seconds before migration is forcibly ended by the watchdog.")]
        [SerializeField]
        [Range(15f, 120f)]
        private float _watchdogTimeout = 45f;

        [Tooltip("Maximum number of client reconnect attempts during migration.")]
        [SerializeField]
        [Range(3, 20)]
        private int _maxReconnectAttempts = 10;

        [Tooltip("Leave the EOS lobby when migration fails (prevents limbo state).")]
        [SerializeField]
        private bool _leaveLobbyOnFailure = true;

        [Tooltip("Stop FishNet when migration fails (prevents running with dead connection).")]
        [SerializeField]
        private bool _stopFishNetOnFailure = true;

        [Header("Debug")]
        [SerializeField]
        private bool _enableDebugLogs = true;

        #endregion

        #region Properties

        /// <summary>
        /// Whether migration is currently in progress.
        /// </summary>
        public bool IsMigrating { get; private set; }

        /// <summary>
        /// Our local ProductUserId.
        /// </summary>
        private string LocalPuid => EOSManager.Instance?.LocalProductUserId?.ToString();

        #endregion

        #region Private Fields

        /// <summary>
        /// All tracked NetworkObjects (auto-tracked, excludes DoNotMigrate).
        /// </summary>
        private readonly List<NetworkObject> _trackedObjects = new();

        /// <summary>
        /// Legacy: Objects with HostMigratable component for backwards compatibility.
        /// </summary>
        private readonly List<HostMigratable> _migratableObjects = new();

        private readonly List<MigratableObjectState> _savedStates = new();
        private readonly Dictionary<string, NetworkObject> _prefabDictionary = new();

        /// <summary>
        /// Auto-tracked objects waiting for their original owner to reconnect.
        /// Key = owner PUID, Value = list of NetworkObjects.
        /// </summary>
        private readonly Dictionary<string, List<NetworkObject>> _pendingAutoRepossessions = new();

        private EOSNativeTransport _transport;
        private EOSLobbyManager _lobbyManager;

        // Reconnect retry state (for clients during migration)
        private int _reconnectAttempt = 0;
        private static readonly float[] RECONNECT_DELAYS = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f, 3f, 3f, 3f, 3f };

        // Watchdog
        private Coroutine _watchdogCoroutine;

        // Timing
        private float _migrationStartTime;
        private bool _wasNewHost;

        // P2P pre-accept state
        private bool _preAcceptedNewHost;
        private string _preAcceptedPuid;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void Start()
        {
            _transport = FindAnyObjectByType<EOSNativeTransport>();
            _lobbyManager = EOSLobbyManager.Instance;

            // Build prefab lookup dictionary from PrefabObjects collection
            if (_prefabCollection != null)
            {
                for (int i = 0; i < _prefabCollection.GetObjectCount(); i++)
                {
                    var prefab = _prefabCollection.GetObject(false, i);
                    if (prefab != null)
                    {
                        RegisterPrefab(prefab);
                    }
                }
            }

            // Also register player prefab from HostMigrationPlayerSpawner if available
            var playerSpawner = FindAnyObjectByType<HostMigrationPlayerSpawner>();
            if (playerSpawner != null && playerSpawner.PlayerPrefab != null)
            {
                Log($"Found player spawner with prefab: {playerSpawner.PlayerPrefab.name}");
                RegisterPrefab(playerSpawner.PlayerPrefab);
            }
            else
            {
                Log($"WARNING: No HostMigrationPlayerSpawner found or no player prefab set!");
            }

            // Subscribe to lobby events
            if (_lobbyManager != null)
            {
                _lobbyManager.OnOwnerChanged += OnLobbyOwnerChanged;
            }

            // Subscribe to client connection state to save states BEFORE objects are despawned
            if (InstanceFinder.ClientManager != null)
            {
                InstanceFinder.ClientManager.OnClientConnectionState += OnClientConnectionState;
            }

            // Note: FishNet doesn't expose global spawn/despawn events on ServerManager.
            // Instead, we scan ServerManager.Objects.Spawned when migration starts.
        }

        /// <summary>
        /// Scans all currently spawned NetworkObjects and populates _trackedObjects.
        /// Called on-demand before saving states since FishNet doesn't expose global spawn/despawn events.
        /// </summary>
        private void ScanSpawnedObjects()
        {
            _trackedObjects.Clear();

            var spawned = InstanceFinder.ServerManager?.Objects?.Spawned;
            if (spawned == null)
            {
                // Not server — try client's spawned objects for pre-save
                spawned = InstanceFinder.ClientManager?.Objects?.Spawned;
            }
            if (spawned == null) return;

            foreach (var kvp in spawned)
            {
                var nob = kvp.Value;
                if (nob == null) continue;
                if (nob.IsSceneObject) continue;
                if (nob.GetComponent<DoNotMigrate>() != null) continue;

                _trackedObjects.Add(nob);

                // Also register the prefab if not already registered
                string prefabName = nob.gameObject.name.Replace("(Clone)", "").Trim();
                if (!_prefabDictionary.ContainsKey(prefabName))
                {
                    var spawnablePrefabs = InstanceFinder.NetworkManager?.SpawnablePrefabs;
                    if (spawnablePrefabs != null)
                    {
                        for (int i = 0; i < spawnablePrefabs.GetObjectCount(); i++)
                        {
                            var prefab = spawnablePrefabs.GetObject(false, i);
                            if (prefab != null && prefab.gameObject.name == prefabName)
                            {
                                _prefabDictionary[prefabName] = prefab;
                                break;
                            }
                        }
                    }
                }
            }

            Log($"Scanned {_trackedObjects.Count} spawned objects for migration tracking");
        }

        /// <summary>
        /// Registers a prefab for migration support.
        /// </summary>
        public void RegisterPrefab(NetworkObject prefab)
        {
            if (prefab == null) return;
            string prefabName = prefab.gameObject.name.Replace("(Clone)", "");
            if (!_prefabDictionary.ContainsKey(prefabName))
            {
                _prefabDictionary[prefabName] = prefab;
                Log($"Registered migratable prefab: {prefabName}");
            }
        }

        private void OnDestroy()
        {
            CancelWatchdog();

            if (_lobbyManager != null)
            {
                _lobbyManager.OnOwnerChanged -= OnLobbyOwnerChanged;
            }

            if (InstanceFinder.ClientManager != null)
            {
                InstanceFinder.ClientManager.OnClientConnectionState -= OnClientConnectionState;
            }

            // No global spawn events to unsubscribe — we use on-demand scanning

            if (_instance == this)
            {
                _instance = null;
            }
        }

        /// <summary>
        /// Handles client connection state changes.
        /// Saves all tracked object states BEFORE they get despawned on disconnect.
        /// </summary>
        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            // We care about Stopping state - this fires BEFORE objects are despawned
            if (args.ConnectionState == LocalConnectionState.Stopping)
            {
                ScanSpawnedObjects();
                int totalObjects = _trackedObjects.Count + _migratableObjects.Count;
                Log($"Client stopping - saving {totalObjects} objects preemptively");

                // Only save if we haven't already saved (avoid double-save)
                if (_savedStates.Count == 0 && totalObjects > 0)
                {
                    // Save auto-tracked objects (new system)
                    foreach (var nob in _trackedObjects)
                    {
                        if (nob == null) continue;

                        var state = SaveNetworkObjectState(nob);
                        if (state.HasValue)
                        {
                            _savedStates.Add(state.Value);
                            Log($"Pre-saved state for: {state.Value.PrefabName} at {state.Value.Position}");
                        }
                    }

                    // Save legacy HostMigratable objects (backwards compatibility)
                    foreach (var migratable in _migratableObjects)
                    {
                        if (migratable == null) continue;

                        // Skip if already saved via auto-tracking
                        if (_trackedObjects.Contains(migratable.NetworkObject)) continue;

                        var state = migratable.SaveDataToStateStruct();
                        _savedStates.Add(state);
                        Log($"Pre-saved state for (legacy): {state.PrefabName} at {state.Position}");
                    }

                    Log($"Pre-saved {_savedStates.Count} object states before disconnect");
                }
            }
        }

        /// <summary>
        /// Saves state from any NetworkObject (with or without HostMigratable).
        /// Uses HostMigratable if present for SyncVar data, otherwise saves basic state.
        /// </summary>
        private MigratableObjectState? SaveNetworkObjectState(NetworkObject nob)
        {
            if (nob == null) return null;

            // If object has HostMigratable, use its save method (includes SyncVars)
            var migratable = nob.GetComponent<HostMigratable>();
            if (migratable != null)
            {
                return migratable.SaveDataToStateStruct();
            }

            // Otherwise, save basic state (position, rotation, owner)
            string ownerPuid = GetOwnerPuid(nob);

            // Only save if we can identify the prefab
            string prefabName = nob.gameObject.name.Replace("(Clone)", "").Trim();
            if (!_prefabDictionary.ContainsKey(prefabName))
            {
                Log($"Cannot save {nob.name}: prefab not registered");
                return null;
            }

            // Capture basic SyncVar data via reflection
            var syncData = CaptureSyncVarData(nob);

            return new MigratableObjectState
            {
                PrefabName = prefabName,
                Position = nob.transform.position,
                Rotation = nob.transform.rotation,
                OwnerPuid = ownerPuid,
                SyncVarData = syncData
            };
        }

        /// <summary>
        /// Gets the owner's PUID from a NetworkObject, normalized for consistent lookups.
        /// </summary>
        private string GetOwnerPuid(NetworkObject nob)
        {
            if (nob.Owner == null) return null;

            string ownerPuid = nob.Owner.GetAddress();

            // Client host doesn't have network address, use local PUID
            if (string.IsNullOrEmpty(ownerPuid) && nob.Owner.IsLocalClient)
            {
                ownerPuid = EOSManager.Instance?.LocalProductUserId?.ToString();
            }

            return NormalizePuid(ownerPuid);
        }

        private static string NormalizePuid(string puid) => PuidUtils.NormalizePuid(puid);

        /// <summary>
        /// Captures all SyncVar data from a NetworkObject and its children via reflection.
        /// </summary>
        private Dictionary<string, Dictionary<string, object>> CaptureSyncVarData(NetworkObject nob)
        {
            var syncData = new Dictionary<string, Dictionary<string, object>>();
            var behaviours = nob.GetComponentsInChildren<NetworkBehaviour>(true);

            foreach (var behaviour in behaviours)
            {
                var type = behaviour.GetType();
                string relPath = GetRelativePath(nob.transform, behaviour.transform);
                string key = $"{relPath}|{type.Name}_{behaviour.GetInstanceID()}";
                var compData = new Dictionary<string, object>();

                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var field in fields)
                {
                    if (field.FieldType.IsGenericType &&
                        field.FieldType.GetGenericTypeDefinition() == typeof(SyncVar<>))
                    {
                        var syncVarInstance = field.GetValue(behaviour);
                        var valueProp = field.FieldType.GetProperty("Value");
                        if (valueProp != null)
                        {
                            var value = valueProp.GetValue(syncVarInstance);
                            compData[field.Name] = value;
                        }
                    }
                }

                if (compData.Count > 0)
                {
                    syncData[key] = compData;
                }
            }

            return syncData.Count > 0 ? syncData : null;
        }

        /// <summary>
        /// Gets the relative path from root to child transform.
        /// </summary>
        private string GetRelativePath(Transform root, Transform child)
        {
            if (child == root) return "ROOT";

            var names = new List<string>();
            var current = child;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Registers a migratable object for tracking.
        /// Called automatically by HostMigratable.OnEnable.
        /// </summary>
        public void AddMigratableObject(HostMigratable migratable)
        {
            if (migratable == null) return;

            // Strip "(Clone)" suffix to match prefab dictionary keys
            string prefabName = migratable.gameObject.name.Replace("(Clone)", "");
            if (!_prefabDictionary.ContainsKey(prefabName))
            {
                // Prefab not registered for migration - this is fine, just skip tracking
                Log($"Object {migratable.gameObject.name} not in prefab dictionary (key: {prefabName})");
                return;
            }

            if (!_migratableObjects.Contains(migratable))
            {
                _migratableObjects.Add(migratable);
                Log($"Tracking migratable: {prefabName}");
            }
        }

        /// <summary>
        /// Unregisters a migratable object.
        /// Called automatically by HostMigratable.OnDisable.
        /// </summary>
        public void RemoveMigratableObject(HostMigratable migratable)
        {
            _migratableObjects.Remove(migratable);
        }

        /// <summary>
        /// Saves an individual object's state when it's being disabled.
        /// Called from HostMigratable.OnDisable to capture state before destruction.
        /// </summary>
        public void SaveObjectStateOnDisable(HostMigratable migratable)
        {
            if (migratable == null) return;

            // Use the effective PUID (cached value if SyncVar is empty)
            // By the time OnDisable runs, FishNet may have already cleared the SyncVar,
            // but the cached PUID is still valid. This matches what SaveDataToStateStruct() does.
            string effectivePuid = !string.IsNullOrEmpty(migratable.OwnerPuidSyncVar.Value)
                ? migratable.OwnerPuidSyncVar.Value
                : migratable.CachedOwnerPuid;

            // Check if we already have a saved state for this PUID
            // (avoid duplicates - pre-saved states from OnClientConnectionState should take precedence)
            bool alreadySaved = false;
            foreach (var state in _savedStates)
            {
                if (state.OwnerPuid == effectivePuid && state.PrefabName == migratable.gameObject.name)
                {
                    alreadySaved = true;
                    break;
                }
            }

            if (alreadySaved)
            {
                Log($"Skipping duplicate save for: {migratable.gameObject.name} (already pre-saved)");
                return;
            }

            var newState = migratable.SaveDataToStateStruct();
            _savedStates.Add(newState);
            Log($"Auto-saved state on disable for: {newState.PrefabName} (owner: {effectivePuid?.Substring(0, Math.Min(8, effectivePuid?.Length ?? 0))}...) at {newState.Position}");
        }

        /// <summary>
        /// Manually trigger migration save (for testing).
        /// </summary>
        public void DebugTriggerSave()
        {
            MigrationDetectedSaveNow();
        }

        /// <summary>
        /// Manually trigger migration finish (for testing).
        /// Simulates becoming the new host.
        /// </summary>
        public void DebugTriggerFinish()
        {
            BeginMigrationSequenceAsHost();
        }

        /// <summary>
        /// Clears all migration state. Call when intentionally leaving a lobby
        /// to prevent stale data from affecting future sessions.
        /// </summary>
        public void ClearMigrationState()
        {
            CancelWatchdog();
            CancelInvoke(nameof(AttemptReconnect));
            _savedStates.Clear();
            _trackedObjects.Clear();
            _migratableObjects.Clear();
            _pendingAutoRepossessions.Clear();
            _preAcceptedNewHost = false;
            _preAcceptedPuid = null;
            HostMigratable.ClearPendingRepossessions();
            IsMigrating = false;
            Log("Migration state cleared");
        }

        /// <summary>
        /// Cancel an in-progress migration. Triggers cleanup if configured.
        /// </summary>
        public void CancelMigration()
        {
            if (!IsMigrating) return;
            Log("Migration cancelled by caller");
            CompleteMigration(MigrationResult.Cancelled);
        }

        /// <summary>
        /// Gets auto-tracked objects pending repossession for a specific owner.
        /// Called by HostMigrationPlayerSpawner when a player reconnects.
        /// </summary>
        public List<NetworkObject> GetPendingAutoRepossessions(string ownerPuid)
        {
            if (_pendingAutoRepossessions.TryGetValue(ownerPuid, out var objects))
            {
                return objects;
            }
            return null;
        }

        /// <summary>
        /// Clears pending auto-repossessions for an owner after they've been claimed.
        /// </summary>
        public void ClearPendingAutoRepossessions(string ownerPuid)
        {
            _pendingAutoRepossessions.Remove(ownerPuid);
        }

        /// <summary>
        /// Total count of tracked objects (auto + legacy).
        /// </summary>
        public int TotalTrackedCount => _trackedObjects.Count + _migratableObjects.Count;

        /// <summary>
        /// Whether there are any auto-tracked objects waiting for repossession.
        /// Used by HostMigrationPlayerSpawner to gate fallback logic.
        /// </summary>
        public bool HasPendingRepossessions => _pendingAutoRepossessions.Count > 0;

        #endregion

        #region Migration Flow

        private void OnLobbyOwnerChanged(string newOwnerPuid)
        {
            bool weAreNewHost = LocalPuid == newOwnerPuid;
            _migrationStartTime = Time.unscaledTime; // Start timing from the moment we detect owner change

            LogTimed($"Owner changed to {newOwnerPuid?.Substring(0, 8)}... (we are new host: {weAreNewHost})");

            // Update transport target for reconnection (both host and clients need this)
            if (_transport != null)
            {
                _transport.RemoteProductUserId = newOwnerPuid;
            }

            if (weAreNewHost)
            {
                // We're the new host - save state and begin migration as host
                MigrationDetectedSaveNow();
                BeginMigrationSequenceAsHost();
            }
            else
            {
                // Pre-accept P2P connection to new host BEFORE tearing down old client.
                // This tells EOS to start NAT traversal / relay negotiation immediately,
                // overlapping with the FishNet shutdown/restart cycle.
                // Critical for Quest/mobile where relay setup can take 5-10s.
                PreAcceptNewHost(newOwnerPuid);

                // We're a client - need to reconnect to the new host
                BeginMigrationSequenceAsClient();
            }
        }

        /// <summary>
        /// Pre-accepts a P2P connection to the new host before tearing down the old connection.
        /// This allows EOS to begin relay/NAT negotiation in parallel with FishNet shutdown.
        /// </summary>
        private void PreAcceptNewHost(string newHostPuid)
        {
            var p2p = EOSManager.Instance?.P2PInterface;
            var localUserId = EOSManager.Instance?.LocalProductUserId;
            if (p2p == null || localUserId == null || string.IsNullOrEmpty(newHostPuid))
            {
                LogTimed("Pre-accept skipped: P2P interface or local user not available");
                return;
            }

            var remoteUserId = ProductUserId.FromString(newHostPuid);
            if (remoteUserId == null || !remoteUserId.IsValid())
            {
                LogTimed($"Pre-accept skipped: invalid new host PUID {newHostPuid}");
                return;
            }

            string socketName = _transport != null ? _transport.SocketName : "FishNetEOS";

            // Accept connection from new host so EOS starts negotiation
            var acceptOptions = new AcceptConnectionOptions
            {
                LocalUserId = localUserId,
                RemoteUserId = remoteUserId,
                SocketId = new SocketId { SocketName = socketName }
            };

            Result acceptResult = p2p.AcceptConnection(ref acceptOptions);
            LogTimed($"Pre-accept P2P to new host {newHostPuid.Substring(0, 8)}...: {acceptResult}");

            if (acceptResult == Result.Success)
            {
                // Send an empty packet to trigger the P2P handshake immediately.
                // Without this, EOS waits for actual data before starting negotiation.
                var sendOptions = new SendPacketOptions
                {
                    LocalUserId = localUserId,
                    RemoteUserId = remoteUserId,
                    SocketId = new SocketId { SocketName = socketName },
                    Channel = 0,
                    Data = new ArraySegment<byte>(new byte[] { 0 }),
                    Reliability = PacketReliability.ReliableOrdered,
                    AllowDelayedDelivery = true,
                    DisableAutoAcceptConnection = false
                };

                Result sendResult = p2p.SendPacket(ref sendOptions);
                LogTimed($"Pre-accept handshake packet to new host: {sendResult}");

                _preAcceptedNewHost = true;
                _preAcceptedPuid = newHostPuid;
            }
        }

        /// <summary>
        /// Save all tracked object states (auto-tracked + legacy HostMigratable).
        /// </summary>
        private void MigrationDetectedSaveNow()
        {
            // Check if states were already pre-saved (by OnDisable auto-save or OnClientConnectionState)
            if (_savedStates.Count > 0)
            {
                Log($"Using {_savedStates.Count} pre-saved states (auto-saved on object disable)");
                OnMigrationStarted?.Invoke();
                return;
            }

            ScanSpawnedObjects();
            int totalObjects = _trackedObjects.Count + _migratableObjects.Count;
            Log($"Saving object states... (auto-tracked: {_trackedObjects.Count}, legacy: {_migratableObjects.Count}, prefabs: {_prefabDictionary.Count})");

            // Log registered prefabs for debugging
            foreach (var kvp in _prefabDictionary)
            {
                Log($"Registered prefab: {kvp.Key}");
            }

            // Save auto-tracked objects (new system)
            foreach (var nob in _trackedObjects)
            {
                if (nob == null) continue;

                var state = SaveNetworkObjectState(nob);
                if (state.HasValue)
                {
                    _savedStates.Add(state.Value);
                    Log($"Saved state for: {state.Value.PrefabName} at {state.Value.Position}");
                }
            }

            // Save legacy HostMigratable objects (backwards compatibility)
            foreach (var migratable in _migratableObjects)
            {
                if (migratable == null) continue;

                // Skip if already saved via auto-tracking
                if (_trackedObjects.Contains(migratable.NetworkObject)) continue;

                var state = migratable.SaveDataToStateStruct();
                _savedStates.Add(state);
                Log($"Saved state for (legacy): {state.PrefabName} at {state.Position}");
            }

            Log($"Saved {_savedStates.Count} object states");
            OnMigrationStarted?.Invoke();
        }

        /// <summary>
        /// Begin the migration sequence AS THE NEW HOST: stop client -> stop server -> start server -> restore -> start client.
        /// </summary>
        private void BeginMigrationSequenceAsHost()
        {
            if (IsMigrating)
            {
                Log("Migration already in progress");
                return;
            }

            IsMigrating = true;
            _wasNewHost = true;
            _migrationStartTime = Time.unscaledTime;
            _reconnectAttempt = 0;
            _preAcceptedNewHost = false;
            _preAcceptedPuid = null;
            StartWatchdog();
            LogTimed("Beginning migration sequence as NEW HOST...");

            // Step 1: Stop client first
            if (InstanceFinder.IsClientStarted)
            {
                InstanceFinder.ClientManager.OnClientConnectionState += OnHostClientStopped;
                InstanceFinder.ClientManager.StopConnection();
            }
            else
            {
                // Client wasn't running, skip to stopping server
                StopOldServer();
            }
        }

        private void OnHostClientStopped(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped) return;

            InstanceFinder.ClientManager.OnClientConnectionState -= OnHostClientStopped;
            LogTimed("Host: Client stopped");

            StopOldServer();
        }

        private void StopOldServer()
        {
            // Step 2: Stop server if we were running one (unlikely but handle it)
            if (InstanceFinder.IsServerStarted)
            {
                InstanceFinder.ServerManager.OnServerConnectionState += OnHostServerStopped;
                InstanceFinder.ServerManager.StopConnection(true);
            }
            else
            {
                // Server wasn't running - start fresh
                StartNewServer();
            }
        }

        private void OnHostServerStopped(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped) return;

            InstanceFinder.ServerManager.OnServerConnectionState -= OnHostServerStopped;
            LogTimed("Host: Old server stopped");

            StartNewServer();
        }

        private void StartNewServer()
        {
            // Reset scene NetworkObjects that still have ObjectId set from client session.
            // When host crashes, client disconnects but scene objects retain their ObjectId.
            // ResetState clears ObjectId to UNSET_OBJECTID_VALUE so server can reinitialize.
            ResetSceneNetworkObjects();

            // Start server as new host
            LogTimed("Host: Starting server...");
            InstanceFinder.ServerManager.OnServerConnectionState += OnHostServerStarted;
            InstanceFinder.ServerManager.StartConnection();
        }

        /// <summary>
        /// Reset scene NetworkObjects so they can be re-initialized by the new server.
        /// </summary>
        private void ResetSceneNetworkObjects()
        {
            // IMPORTANT: Include inactive objects - scene objects get deactivated when client disconnects
            var sceneObjects = FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            EOSDebugLogger.Log(DebugCategory.HostMigrationManager, "HostMigrationManager", $"ResetSceneNetworkObjects: Found {sceneObjects.Length} NetworkObjects (including inactive)");

            int resetCount = 0;
            int sceneObjectCount = 0;

            foreach (var nob in sceneObjects)
            {
                // Only reset scene objects (not spawned prefabs)
                if (!nob.IsSceneObject) continue;
                sceneObjectCount++;

                EOSDebugLogger.Log(DebugCategory.HostMigrationManager, "HostMigrationManager", $"Scene object {nob.name}: ObjectId={nob.ObjectId}, UNSET={NetworkObject.UNSET_OBJECTID_VALUE}, active={nob.gameObject.activeSelf}");

                // Check if ObjectId is still set (not UNSET) - indicates leftover state
                // UNSET_OBJECTID_VALUE = ushort.MaxValue (65535)
                if (nob.ObjectId == NetworkObject.UNSET_OBJECTID_VALUE)
                {
                    EOSDebugLogger.Log(DebugCategory.HostMigrationManager, "HostMigrationManager", $"Skipping {nob.name}: ObjectId already UNSET ({nob.ObjectId})");
                    continue;
                }

                try
                {
                    int oldObjectId = nob.ObjectId;
                    // ResetState clears ObjectId at the end of the method
                    nob.ResetState(asServer: false);
                    EOSDebugLogger.Log(DebugCategory.HostMigrationManager, "HostMigrationManager", $"Reset {nob.name}: ObjectId was {oldObjectId}, now is {nob.ObjectId}");
                    resetCount++;
                }
                catch (System.Exception e)
                {
                    EOSDebugLogger.LogError("HostMigrationManager", $"EXCEPTION resetting {nob.name}: {e}");
                }
            }

            EOSDebugLogger.Log(DebugCategory.HostMigrationManager, "HostMigrationManager", $"Host: Found {sceneObjectCount} scene objects, reset {resetCount}");
        }

        private void OnHostServerStarted(ServerConnectionStateArgs args)
        {
            // Handle both Started and Stopped (failure) states
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                InstanceFinder.ServerManager.OnServerConnectionState -= OnHostServerStarted;
                LogTimed("Host: Server started");

                // Step 4: Restore migratable objects
                MigrationFinish();
                LogTimed("Host: Objects restored");

                // Step 5: Start client (clienthost)
                InstanceFinder.ClientManager.StartConnection();
                LogTimed("Host: ClientHost started");

                // Rebuild observers
                InstanceFinder.ServerManager.Objects.RebuildObservers();

                CompleteMigration(MigrationResult.Success);
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                InstanceFinder.ServerManager.OnServerConnectionState -= OnHostServerStarted;
                LogTimed("Host: Server FAILED to start");
                CompleteMigration(MigrationResult.ServerStartFailed);
            }
        }

        /// <summary>
        /// Begin the migration sequence AS A CLIENT: stop client -> wait -> reconnect.
        /// </summary>
        private void BeginMigrationSequenceAsClient()
        {
            if (IsMigrating)
            {
                Log("Migration already in progress");
                return;
            }

            IsMigrating = true;
            _wasNewHost = false;
            _migrationStartTime = Time.unscaledTime;
            _reconnectAttempt = 0;
            StartWatchdog();
            LogTimed($"Beginning migration sequence as CLIENT (pre-accepted: {_preAcceptedNewHost})...");
            OnMigrationStarted?.Invoke();

            // Step 1: Stop client connection
            if (InstanceFinder.IsClientStarted)
            {
                InstanceFinder.ClientManager.OnClientConnectionState += OnClientClientStopped;
                InstanceFinder.ClientManager.StopConnection();
            }
            else
            {
                // Client wasn't running - just try to connect
                ScheduleReconnect();
            }
        }

        private void OnClientClientStopped(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped) return;

            InstanceFinder.ClientManager.OnClientConnectionState -= OnClientClientStopped;
            LogTimed("Client: Disconnected from old host");

            ScheduleReconnect();
        }

        /// <summary>
        /// Schedules reconnection with retry logic for clients.
        /// The new host needs time to complete their migration.
        /// </summary>
        private void ScheduleReconnect()
        {
            _reconnectAttempt = 0;

            // If we pre-accepted the new host, P2P negotiation is already underway.
            // Try sooner since the relay/NAT handshake had a head start.
            float initialDelay = _preAcceptedNewHost ? 0.5f : 1.5f;
            LogTimed($"Client: Scheduling first reconnect in {initialDelay}s (pre-accepted: {_preAcceptedNewHost})");

            Invoke(nameof(AttemptReconnect), initialDelay);
        }

        private void AttemptReconnect()
        {
            // Safety: if migration was already completed/cancelled, don't proceed
            if (!IsMigrating) return;

            _reconnectAttempt++;

            // Check lobby membership before wasting a reconnect attempt
            if (_lobbyManager != null && !_lobbyManager.IsInLobby)
            {
                LogTimed("Client: No longer in lobby — cannot reconnect");
                CompleteMigration(MigrationResult.LobbyMembershipLost);
                return;
            }

            if (_reconnectAttempt > _maxReconnectAttempts)
            {
                LogTimed($"Client: Failed to reconnect after {_maxReconnectAttempts} attempts");
                CompleteMigration(MigrationResult.ReconnectFailed);
                return;
            }

            LogTimed($"Client: Reconnect attempt {_reconnectAttempt}/{_maxReconnectAttempts}...");

            // Try to start client connection
            InstanceFinder.ClientManager.OnClientConnectionState += OnReconnectResult;
            InstanceFinder.ClientManager.StartConnection();
        }

        private void OnReconnectResult(ClientConnectionStateArgs args)
        {
            // Only handle terminal states
            if (args.ConnectionState != LocalConnectionState.Started &&
                args.ConnectionState != LocalConnectionState.Stopped)
            {
                return;
            }

            InstanceFinder.ClientManager.OnClientConnectionState -= OnReconnectResult;

            // Safety: if migration was already completed/cancelled, don't proceed
            if (!IsMigrating) return;

            if (args.ConnectionState == LocalConnectionState.Started)
            {
                // Success!
                LogTimed("Client: Successfully reconnected to new host");
                _preAcceptedNewHost = false;
                _preAcceptedPuid = null;
                CompleteMigration(MigrationResult.Success);
            }
            else
            {
                // Failed - try again with delay
                int delayIndex = Math.Min(_reconnectAttempt, RECONNECT_DELAYS.Length - 1);
                float delay = RECONNECT_DELAYS[delayIndex];

                LogTimed($"Client: Connection failed, retrying in {delay}s...");
                Invoke(nameof(AttemptReconnect), delay);
            }
        }

        /// <summary>
        /// Restore all saved object states after server restart.
        /// Handles both HostMigratable objects (with SyncVar restoration) and plain NetworkObjects.
        /// </summary>
        private void MigrationFinish()
        {
            Log($"Restoring {_savedStates.Count} objects...");

            // Clean up any existing pending repossessions from previous migrations
            // These objects weren't claimed and would otherwise accumulate
            if (HostMigratable.PendingRepossessions.Count > 0)
            {
                Log($"Cleaning up {HostMigratable.PendingRepossessions.Count} unclaimed pending repossessions");
                foreach (var kvp in HostMigratable.PendingRepossessions)
                {
                    foreach (var migratable in kvp.Value)
                    {
                        if (migratable != null && migratable.NetworkObject != null)
                        {
                            Log($"Despawning unclaimed object: {migratable.gameObject.name}");
                            InstanceFinder.ServerManager.Despawn(migratable.NetworkObject);
                        }
                    }
                }
                HostMigratable.ClearPendingRepossessions();
            }

            // Also clean up pending repossessions for auto-tracked objects
            _pendingAutoRepossessions.Clear();

            // Iterate over a copy because SetActive(false) triggers OnDisable -> SaveObjectStateOnDisable
            // which would modify _savedStates during enumeration
            var statesToRestore = new List<MigratableObjectState>(_savedStates);
            _savedStates.Clear();

            foreach (var state in statesToRestore)
            {
                if (!_prefabDictionary.TryGetValue(state.PrefabName, out var prefab))
                {
                    Debug.LogWarning($"[HostMigrationManager] Prefab not found: {state.PrefabName}");
                    continue;
                }

                // Spawn the object
                var go = Instantiate(prefab, state.Position, state.Rotation);

                // Check if object has HostMigratable (for backwards compatibility)
                var migratable = go.GetComponent<HostMigratable>();
                if (migratable != null)
                {
                    // Use legacy HostMigratable restoration path
                    migratable.LoadState = state;
                    InstanceFinder.ServerManager.Spawn(go, null);
                    migratable.LoadDataFromStateStruct(state);
                    migratable.LoadState = null;

                    // Register for repossession (normalize PUID for consistent lookups)
                    string normalizedLegacyPuid = NormalizePuid(state.OwnerPuid);
                    if (!string.IsNullOrEmpty(normalizedLegacyPuid))
                    {
                        if (!HostMigratable.PendingRepossessions.ContainsKey(normalizedLegacyPuid))
                        {
                            HostMigratable.PendingRepossessions[normalizedLegacyPuid] = new List<HostMigratable>();
                        }
                        HostMigratable.PendingRepossessions[normalizedLegacyPuid].Add(migratable);
                        migratable.gameObject.SetActive(false);
                        Log($"Registered for repossession (legacy): {state.PrefabName} for owner {normalizedLegacyPuid.Substring(0, 8)}...");
                    }
                    else
                    {
                        Log($"Restored (legacy): {state.PrefabName} (no owner)");
                    }
                }
                else
                {
                    // New auto-migration path: restore without HostMigratable
                    var nob = go.GetComponent<NetworkObject>();
                    InstanceFinder.ServerManager.Spawn(go, null);

                    // Restore SyncVar data
                    if (state.SyncVarData != null)
                    {
                        RestoreSyncVarData(nob, state);
                    }

                    // Register for repossession (normalize PUID for consistent lookups)
                    string normalizedAutoPuid = NormalizePuid(state.OwnerPuid);
                    if (!string.IsNullOrEmpty(normalizedAutoPuid))
                    {
                        if (!_pendingAutoRepossessions.ContainsKey(normalizedAutoPuid))
                        {
                            _pendingAutoRepossessions[normalizedAutoPuid] = new List<NetworkObject>();
                        }
                        _pendingAutoRepossessions[normalizedAutoPuid].Add(nob);
                        go.gameObject.SetActive(false);
                        Log($"Registered for repossession (auto): {state.PrefabName} for owner {normalizedAutoPuid.Substring(0, 8)}...");
                    }
                    else
                    {
                        Log($"Restored (auto): {state.PrefabName} (no owner)");
                    }
                }
            }
        }

        /// <summary>
        /// Restores SyncVar data to a NetworkObject without HostMigratable.
        /// </summary>
        private void RestoreSyncVarData(NetworkObject nob, MigratableObjectState state)
        {
            if (state.SyncVarData == null || nob == null) return;

            foreach (var kvp in state.SyncVarData)
            {
                string[] parts = kvp.Key.Split('|');
                if (parts.Length != 2) continue;

                string relPath = parts[0];
                string[] typeParts = parts[1].Split('_');
                if (typeParts.Length < 2) continue;

                string typeName = typeParts[0];
                var targetTransform = relPath == "ROOT" ? nob.transform : nob.transform.Find(relPath);
                if (targetTransform == null) continue;

                var comps = targetTransform.GetComponents<NetworkBehaviour>();
                NetworkBehaviour targetComp = null;
                foreach (var comp in comps)
                {
                    if (comp.GetType().Name == typeName)
                    {
                        targetComp = comp;
                        break;
                    }
                }

                if (targetComp == null) continue;

                var compData = kvp.Value;
                var fields = targetComp.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var field in fields)
                {
                    if (field.FieldType.IsGenericType &&
                        field.FieldType.GetGenericTypeDefinition() == typeof(SyncVar<>))
                    {
                        if (compData.TryGetValue(field.Name, out var value))
                        {
                            var syncVarInstance = field.GetValue(targetComp);
                            var valueProp = field.FieldType.GetProperty("Value");
                            valueProp?.SetValue(syncVarInstance, value);
                            Log($"  Restored {field.Name} = {value}");
                        }
                    }
                }
            }
        }

        #endregion

        #region Centralized Migration Completion

        /// <summary>
        /// Single method that handles ALL migration endings — success, failure, timeout, cancellation.
        /// Cancels watchdog, fires events, triggers cleanup on failure.
        /// </summary>
        private void CompleteMigration(MigrationResult result)
        {
            if (!IsMigrating) return; // Already completed (guard against double-fire)

            CancelWatchdog();
            CancelInvoke(nameof(AttemptReconnect));

            float elapsed = Time.unscaledTime - _migrationStartTime;
            bool success = result == MigrationResult.Success;

            IsMigrating = false;

            var args = new MigrationFinishedArgs
            {
                Success = success,
                Result = result,
                WasNewHost = _wasNewHost,
                ElapsedSeconds = elapsed,
                ReconnectAttempts = _reconnectAttempt
            };

            if (success)
            {
                LogTimed($"Migration completed SUCCESSFULLY in {elapsed:F1}s (host={_wasNewHost}, attempts={_reconnectAttempt})");
            }
            else
            {
                LogTimed($"Migration FAILED: {result} after {elapsed:F1}s (host={_wasNewHost}, attempts={_reconnectAttempt})");
                HandleMigrationFailure();
            }

            _preAcceptedNewHost = false;
            _preAcceptedPuid = null;

            // Fire detailed event first, then legacy event for backward compat
            OnMigrationFinished?.Invoke(args);
            OnMigrationCompleted?.Invoke();
        }

        /// <summary>
        /// Cleans up after a failed migration to prevent the limbo state
        /// where the user is stuck in an EOS lobby with a dead FishNet connection.
        /// </summary>
        private void HandleMigrationFailure()
        {
            if (_stopFishNetOnFailure)
            {
                Log("Stopping FishNet due to migration failure");
                if (InstanceFinder.IsServerStarted)
                    InstanceFinder.ServerManager.StopConnection(true);
                if (InstanceFinder.IsClientStarted)
                    InstanceFinder.ClientManager.StopConnection();
            }

            if (_leaveLobbyOnFailure && _lobbyManager != null && _lobbyManager.IsInLobby)
            {
                Log("Leaving lobby due to migration failure");
                _ = _transport?.LeaveLobbyAsync();
            }

            // Clear saved states so they don't pollute next session
            _savedStates.Clear();
        }

        #endregion

        #region Watchdog

        private void StartWatchdog()
        {
            CancelWatchdog();
            _watchdogCoroutine = StartCoroutine(WatchdogCoroutine());
        }

        private void CancelWatchdog()
        {
            if (_watchdogCoroutine != null)
            {
                StopCoroutine(_watchdogCoroutine);
                _watchdogCoroutine = null;
            }
        }

        private IEnumerator WatchdogCoroutine()
        {
            yield return new WaitForSecondsRealtime(_watchdogTimeout);

            if (IsMigrating)
            {
                LogTimed($"WATCHDOG: Migration exceeded {_watchdogTimeout}s timeout — forcing completion");
                CompleteMigration(MigrationResult.WatchdogTimeout);
            }

            _watchdogCoroutine = null;
        }

        #endregion

        #region Logging

        private void Log(string message)
        {
            if (!_enableDebugLogs) return;
            EOSDebugLogger.Log(DebugCategory.HostMigrationManager, "HostMigrationManager", message);
        }

        /// <summary>
        /// Logs with elapsed time since migration started. Use for migration flow diagnostics.
        /// Quest users can report these timestamps to identify exactly where delays occur.
        /// </summary>
        private void LogTimed(string message)
        {
            if (!_enableDebugLogs) return;
            float elapsed = _migrationStartTime > 0f ? Time.unscaledTime - _migrationStartTime : 0f;
            EOSDebugLogger.Log(DebugCategory.HostMigrationManager, "HostMigrationManager",
                $"[T+{elapsed:F2}s] {message}");
        }

        #endregion

        #region Editor Access (for inspector)

#if UNITY_EDITOR
        internal int TrackedObjectCount => _trackedObjects.Count;
        internal int LegacyMigratableCount => _migratableObjects.Count;
        internal int SavedStateCount => _savedStates.Count;
        internal int PrefabCount => _prefabDictionary.Count;
        internal int PendingAutoRepossessionCount => _pendingAutoRepossessions.Count;
#endif

        #endregion
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(HostMigrationManager))]
    public class HostMigrationManagerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var manager = (HostMigrationManager)target;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledGroupScope(true))
            {
                // Migration state
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel("Migration");
                var migrationStyle = new GUIStyle(EditorStyles.label);
                migrationStyle.normal.textColor = manager.IsMigrating ? Color.yellow : Color.green;
                EditorGUILayout.LabelField(manager.IsMigrating ? "IN PROGRESS" : "Idle", migrationStyle);
                EditorGUILayout.EndHorizontal();

                // Tracked objects
                EditorGUILayout.IntField("Auto-Tracked Objects", manager.TrackedObjectCount);
                EditorGUILayout.IntField("Legacy (HostMigratable)", manager.LegacyMigratableCount);
                EditorGUILayout.IntField("Saved States", manager.SavedStateCount);
                EditorGUILayout.IntField("Registered Prefabs", manager.PrefabCount);
                EditorGUILayout.IntField("Pending Auto-Repossess", manager.PendingAutoRepossessionCount);
            }

            if (Application.isPlaying)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Debug Controls", EditorStyles.boldLabel);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Save States"))
                {
                    manager.DebugTriggerSave();
                }
                if (GUILayout.Button("Finish Migration"))
                {
                    manager.DebugTriggerFinish();
                }
                if (GUILayout.Button("Cancel Migration"))
                {
                    manager.CancelMigration();
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.HelpBox("Use these buttons to test migration flow. 'Save States' captures current object state. 'Finish Migration' restores saved states.", MessageType.Info);

                EditorUtility.SetDirty(target);
            }
            else
            {
                EditorGUILayout.HelpBox("Enter Play Mode to see runtime status and debug controls.", MessageType.Info);
            }
        }
    }
#endif
}
