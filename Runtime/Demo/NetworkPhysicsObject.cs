using FishNet.Connection;
using FishNet.Object;
using EOSNative.Logging;
using UnityEngine;

namespace FishNet.Transport.EOSNative.Demo
{
    /// <summary>
    /// Makes a networked rigidbody interactive for all clients.
    /// - Forces non-kinematic so physics works locally
    /// - Steals ownership on collision with player for responsive interaction
    /// - Works with PhysicsNetworkTransform for position sync
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkPhysicsObject : NetworkBehaviour
    {
        [Header("Physics")]
        [SerializeField] private bool _forceNonKinematic = true;

        [Header("Ownership")]
        [SerializeField] private bool _stealOwnershipOnCollision = true;
        [SerializeField] private float _minCollisionForce = 0.5f;
        [SerializeField] private float _ownershipCooldown = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool _showDebugLogs = false;

        private Rigidbody _rb;
        private float _lastOwnershipChangeTime;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            if (_forceNonKinematic)
                _rb.isKinematic = false;
        }

        private void FixedUpdate()
        {
            if (!IsSpawned) return;
            if (_forceNonKinematic && _rb.isKinematic)
                _rb.isKinematic = false;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsSpawned || !_stealOwnershipOnCollision) return;
            if (Time.time < _lastOwnershipChangeTime + _ownershipCooldown) return;

            var playerBall = collision.gameObject.GetComponent<PlayerBall>();
            if (playerBall == null || !playerBall.IsOwner) return;
            if (collision.relativeVelocity.magnitude < _minCollisionForce) return;
            if (IsOwner) return;

            RequestOwnership();
        }

        private void OnCollisionStay(Collision collision)
        {
            if (!IsSpawned || !_stealOwnershipOnCollision) return;
            if (Time.time < _lastOwnershipChangeTime + _ownershipCooldown * 2f) return;

            var playerBall = collision.gameObject.GetComponent<PlayerBall>();
            if (playerBall == null || !playerBall.IsOwner || IsOwner) return;
            if (collision.relativeVelocity.magnitude < _minCollisionForce * 0.5f) return;

            RequestOwnership();
        }

        public void RequestOwnership()
        {
            if (!IsSpawned || IsOwner) return;

            if (_showDebugLogs)
                EOSDebugLogger.Log(DebugCategory.NetworkPhysicsObject, "NetworkPhysicsObject", $"Requesting ownership of {gameObject.name}");

            _lastOwnershipChangeTime = Time.time;
            ServerRequestOwnership();
        }

        [ServerRpc(RequireOwnership = false)]
        private void ServerRequestOwnership(NetworkConnection caller = null)
        {
            if (caller == null) return;

            if (_showDebugLogs)
                EOSDebugLogger.Log(DebugCategory.NetworkPhysicsObject, "NetworkPhysicsObject", $"Server granting ownership of {gameObject.name} to {caller.ClientId}");

            GiveOwnership(caller);
        }

        public void ReleaseOwnership()
        {
            if (!IsSpawned || !IsOwner) return;
            NetworkObject.RemoveOwnership();
        }
    }
}
