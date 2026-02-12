using FishNet.Managing;
using FishNet.Object;
using UnityEngine;

namespace FishNet.Transport.EOSNative.Demo
{
    /// <summary>
    /// Spawns crate prefabs when the server starts.
    /// FishNet NetworkObjects must be server-spawned at runtime, not placed in the scene.
    /// </summary>
    public class DemoCrateSpawner : MonoBehaviour
    {
        [Header("Prefab")]
        [SerializeField] private NetworkObject _cratePrefab;

        [Header("Spawning")]
        [SerializeField] private int _crateCount = 5;
        [SerializeField] private float _spawnRadius = 8f;
        [SerializeField] private float _spawnHeight = 1f;

        private bool _hasSpawned;

        private void Update()
        {
            if (_hasSpawned) return;

            var nm = InstanceFinder.NetworkManager;
            if (nm == null || !nm.IsServerStarted) return;

            _hasSpawned = true;
            SpawnCrates(nm);
        }

        private void SpawnCrates(NetworkManager nm)
        {
            if (_cratePrefab == null)
            {
                Debug.LogWarning("[DemoCrateSpawner] Crate prefab not assigned.");
                return;
            }

            for (int i = 0; i < _crateCount; i++)
            {
                float x = Random.Range(-_spawnRadius, _spawnRadius);
                float z = Random.Range(-_spawnRadius, _spawnRadius);
                var pos = new Vector3(x, _spawnHeight, z);

                var crate = Instantiate(_cratePrefab, pos, Quaternion.identity);
                crate.name = $"Crate_{i}";
                nm.ServerManager.Spawn(crate);
            }

            Debug.Log($"[DemoCrateSpawner] Spawned {_crateCount} crates.");
        }
    }
}
