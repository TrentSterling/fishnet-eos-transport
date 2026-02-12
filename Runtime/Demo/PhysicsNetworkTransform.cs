using System;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using EOSNative.Logging;
using UnityEngine;

namespace FishNet.Transport.EOSNative.Demo
{
    /// <summary>
    /// Spring-based physics network transform that allows objects to interact
    /// with local physics while staying synced across the network.
    ///
    /// Unlike ownership-based approaches, this uses damped spring forces to guide
    /// the rigidbody toward the networked position, allowing natural physics interactions.
    ///
    /// Credit: DrewMileham (original method), Skylar/CometDev (https://github.com/SkylarSDev) (Mirror implementation)
    /// Ported to FishNet for FishNet-EOS-Native
    /// </summary>
    public enum PhysicsNetworkTransformMode { Physics, VisualTweened, Visual }

    /// <summary>
    /// Half-precision Vector3 for bandwidth-efficient position sync (6 bytes vs 12).
    /// </summary>
    public struct Vector3Half
    {
        public ushort x, y, z;

        public Vector3Half(Vector3 v)
        {
            x = Mathf.FloatToHalf(v.x);
            y = Mathf.FloatToHalf(v.y);
            z = Mathf.FloatToHalf(v.z);
        }

        public Vector3 ToVector3()
        {
            return new Vector3(
                Mathf.HalfToFloat(x),
                Mathf.HalfToFloat(y),
                Mathf.HalfToFloat(z)
            );
        }
    }

    public class PhysicsNetworkTransform : NetworkBehaviour
    {
        public Rigidbody rigidbodyToTrack;

        [Header("Networking Settings")]
        [Range(0, 1)] public float updateRate = 0;
        private float updateCounter = 0;

        [Tooltip("Minimum position change before sending update")]
        public float positionThreshold = 0.001f;
        [Tooltip("Minimum rotation change (degrees) before sending update")]
        public float rotationThreshold = 0.1f;

        private Vector3 lastSentPosition;
        private Quaternion lastSentRotation;

        [Header("Tracking Settings")]
        public PhysicsNetworkTransformMode mode;
        public float tweenConstant = 10f;

        [Tooltip("For non-authority instances, use this mode instead")]
        public PhysicsNetworkTransformMode nonAuthorityMode = PhysicsNetworkTransformMode.Physics;
        [Tooltip("If true, non-authority uses nonAuthorityMode instead of regular mode")]
        public bool useNonAuthorityMode = false;

        [Header("Distance-Based Mode Switching")]
        [Tooltip("Enable automatic mode switching based on distance from main camera")]
        public bool useDistanceBasedMode = false;
        [Tooltip("Distance below which Physics mode is used")]
        public float physicsDistance = 5f;
        [Tooltip("Distance below which VisualTweened mode is used")]
        public float tweenedDistance = 15f;

        private Camera mainCamera;
        private PhysicsNetworkTransformMode baseMode;

        private new bool HasAuthority => IsOwner || (IsServerInitialized && (Owner == null || !Owner.IsActive));

        [Header("Physics Spring Settings")]
        public float positionSpringFrequency = 4f;
        public float positionDampingRatio = 0.65f;
        public float rotationSpringFrequency = 5.5f;
        public float rotationDampingRatio = 0.55f;

        [Header("Ownership Settings")]
        [Tooltip("Claim ownership when a player collides with this object")]
        public bool claimOwnershipOnCollision = true;
        [Tooltip("Minimum collision force required to claim ownership")]
        public float minCollisionForce = 0.5f;
        [Tooltip("Cooldown between ownership changes")]
        public float ownershipCooldown = 0.25f;
        private float lastOwnershipChangeTime;

        [Header("Debug")]
        public bool updatePositionLocally = false;
        public bool useExternalPosition = false;
        public Vector3 receivedPosition;
        public Quaternion receivedRotation;

        private void Awake()
        {
            if (rigidbodyToTrack == null)
                rigidbodyToTrack = GetComponent<Rigidbody>();
        }

        private void Start()
        {
            mainCamera = Camera.main;
            baseMode = mode;

            if (rigidbodyToTrack != null)
            {
                receivedPosition = rigidbodyToTrack.position;
                receivedRotation = rigidbodyToTrack.rotation;
            }
            else
            {
                EOSDebugLogger.LogError("PhysicsNetworkTransform", $"No Rigidbody found on {gameObject.name}!");
            }
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
        }

        private void FixedUpdate()
        {
            if (rigidbodyToTrack == null) return;
            if (!IsSpawned) return;
            if (!CanRunNetworkUpdates()) return;

            if (HasAuthority)
            {
                if (updatePositionLocally)
                {
                    UpdateModeBasedOnDistance();
                    UpdateTracking();
                }

                if (updateCounter > updateRate)
                {
                    if (useExternalPosition)
                    {
                        BroadcastPosition(CompressPosition(receivedPosition), CompressRotation(receivedRotation));
                    }
                    else
                    {
                        Vector3 currentPos = rigidbodyToTrack.position;
                        Quaternion currentRot = rigidbodyToTrack.rotation;

                        bool positionChanged = Vector3.SqrMagnitude(currentPos - lastSentPosition) > positionThreshold * positionThreshold;
                        bool rotationChanged = Quaternion.Angle(currentRot, lastSentRotation) > rotationThreshold;

                        if (positionChanged || rotationChanged)
                        {
                            BroadcastPosition(CompressPosition(currentPos), CompressRotation(currentRot));
                            lastSentPosition = currentPos;
                            lastSentRotation = currentRot;
                        }
                    }
                    updateCounter = 0;
                }
                updateCounter += Time.fixedDeltaTime;
            }
            else
            {
                UpdateModeBasedOnDistance();
                UpdateTracking();
            }
        }

        private bool CanRunNetworkUpdates()
        {
            if (!InstanceFinder.IsClientStarted && !InstanceFinder.IsServerStarted) return false;
            if (InstanceFinder.IsServerStarted && IsServerInitialized) return true;
            if (InstanceFinder.IsClientStarted && IsClientInitialized) return true;
            return false;
        }

        private void BroadcastPosition(Vector3Half position, uint rotation)
        {
            if (IsServerInitialized && InstanceFinder.IsServerStarted)
            {
                ReceiveDataObserversRpc(position, rotation);
            }
            else if (IsClientInitialized && InstanceFinder.IsClientStarted)
            {
                SendDataServerRpc(position, rotation);
            }
        }

        #region Compression

        private Vector3Half CompressPosition(Vector3 pos) => new Vector3Half(pos);
        private Vector3 DecompressPosition(Vector3Half compressed) => compressed.ToVector3();

        private uint CompressRotation(Quaternion rot)
        {
            float absX = Mathf.Abs(rot.x);
            float absY = Mathf.Abs(rot.y);
            float absZ = Mathf.Abs(rot.z);
            float absW = Mathf.Abs(rot.w);

            int largestIndex = 0;
            float largestValue = absX;

            if (absY > largestValue) { largestIndex = 1; largestValue = absY; }
            if (absZ > largestValue) { largestIndex = 2; largestValue = absZ; }
            if (absW > largestValue) { largestIndex = 3; }

            float a, b, c;
            switch (largestIndex)
            {
                case 0: a = rot.y; b = rot.z; c = rot.w; if (rot.x < 0) { a = -a; b = -b; c = -c; } break;
                case 1: a = rot.x; b = rot.z; c = rot.w; if (rot.y < 0) { a = -a; b = -b; c = -c; } break;
                case 2: a = rot.x; b = rot.y; c = rot.w; if (rot.z < 0) { a = -a; b = -b; c = -c; } break;
                default: a = rot.x; b = rot.y; c = rot.z; if (rot.w < 0) { a = -a; b = -b; c = -c; } break;
            }

            const float scale = 1023f / 1.41421356f;
            uint ua = (uint)Mathf.Clamp((int)((a + 0.707106781f) * scale), 0, 1023);
            uint ub = (uint)Mathf.Clamp((int)((b + 0.707106781f) * scale), 0, 1023);
            uint uc = (uint)Mathf.Clamp((int)((c + 0.707106781f) * scale), 0, 1023);

            return ((uint)largestIndex << 30) | (ua << 20) | (ub << 10) | uc;
        }

        private Quaternion DecompressRotation(uint compressed)
        {
            int largestIndex = (int)(compressed >> 30);
            const float scale = 1.41421356f / 1023f;

            float a = ((compressed >> 20) & 1023) * scale - 0.707106781f;
            float b = ((compressed >> 10) & 1023) * scale - 0.707106781f;
            float c = (compressed & 1023) * scale - 0.707106781f;

            float largest = Mathf.Sqrt(1f - a * a - b * b - c * c);

            switch (largestIndex)
            {
                case 0: return new Quaternion(largest, a, b, c);
                case 1: return new Quaternion(a, largest, b, c);
                case 2: return new Quaternion(a, b, largest, c);
                default: return new Quaternion(a, b, c, largest);
            }
        }

        #endregion

        #region Network RPCs

        [ServerRpc(RequireOwnership = false)]
        private void SendDataServerRpc(Vector3Half position, uint rotation)
        {
            ReceiveDataObserversRpc(position, rotation);
        }

        [ObserversRpc(BufferLast = true, ExcludeServer = false)]
        private void ReceiveDataObserversRpc(Vector3Half position, uint rotation)
        {
            if (HasAuthority) return;

            receivedPosition = DecompressPosition(position);
            receivedRotation = DecompressRotation(rotation);
        }

        #endregion

        #region Mode & Tracking

        private void UpdateModeBasedOnDistance()
        {
            if (!useDistanceBasedMode || mainCamera == null) return;

            float sqrDistance = (mainCamera.transform.position - rigidbodyToTrack.position).sqrMagnitude;

            if (sqrDistance < physicsDistance * physicsDistance)
                mode = PhysicsNetworkTransformMode.Physics;
            else if (sqrDistance < tweenedDistance * tweenedDistance)
                mode = PhysicsNetworkTransformMode.VisualTweened;
            else
                mode = PhysicsNetworkTransformMode.Visual;
        }

        private void UpdateTracking()
        {
            var activeMode = (useNonAuthorityMode && !HasAuthority) ? nonAuthorityMode : mode;

            switch (activeMode)
            {
                case PhysicsNetworkTransformMode.Physics: ApplyPhysicsSpring(); break;
                case PhysicsNetworkTransformMode.VisualTweened: ApplyVisualTweened(); break;
                case PhysicsNetworkTransformMode.Visual: ApplyVisualSnap(); break;
            }
        }

        private void ApplyPhysicsSpring()
        {
            rigidbodyToTrack.isKinematic = false;

            float mass = rigidbodyToTrack.mass;
            float omega = 2f * Mathf.PI * positionSpringFrequency;
            float positionSpring = mass * omega * omega;
            float positionDamper = 2f * mass * positionDampingRatio * omega;

            Vector3 positionError = receivedPosition - rigidbodyToTrack.position;
            Vector3 velocityError = -rigidbodyToTrack.linearVelocity;
            Vector3 springForce = positionError * positionSpring + velocityError * positionDamper;

            if (!IsValidVector(springForce)) return;
            rigidbodyToTrack.AddForce(springForce, ForceMode.Force);

            float inertia = rigidbodyToTrack.inertiaTensor.magnitude;
            float rotOmega = 2f * Mathf.PI * rotationSpringFrequency;
            float rotationSpring = inertia * rotOmega * rotOmega;
            float rotationDamper = 2f * inertia * rotationDampingRatio * rotOmega;

            Quaternion rotationDelta = receivedRotation * Quaternion.Inverse(rigidbodyToTrack.rotation);
            if (rotationDelta.w < 0f)
                rotationDelta = new Quaternion(-rotationDelta.x, -rotationDelta.y, -rotationDelta.z, -rotationDelta.w);
            rotationDelta.ToAngleAxis(out float angle, out Vector3 axis);

            Vector3 angularError = (angle < 0.001f || float.IsNaN(axis.x))
                ? Vector3.zero
                : axis.normalized * (angle * Mathf.Deg2Rad);

            Vector3 springTorque = angularError * rotationSpring + (-rigidbodyToTrack.angularVelocity) * rotationDamper;
            if (!IsValidVector(springTorque)) return;
            rigidbodyToTrack.AddTorque(springTorque, ForceMode.Force);
        }

        private static bool IsValidVector(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
                   !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }

        private void ApplyVisualTweened()
        {
            rigidbodyToTrack.isKinematic = true;
            rigidbodyToTrack.MovePosition(Vector3.Lerp(rigidbodyToTrack.position, receivedPosition, Time.deltaTime * tweenConstant));
            rigidbodyToTrack.MoveRotation(Quaternion.Lerp(rigidbodyToTrack.rotation, receivedRotation, Time.deltaTime * tweenConstant));
        }

        private void ApplyVisualSnap()
        {
            rigidbodyToTrack.isKinematic = true;
            rigidbodyToTrack.MovePosition(receivedPosition);
            rigidbodyToTrack.MoveRotation(receivedRotation);
        }

        #endregion

        #region Ownership

        private void OnCollisionEnter(Collision collision)
        {
            if (!claimOwnershipOnCollision) return;
            if (Time.time - lastOwnershipChangeTime < ownershipCooldown) return;
            if (collision.relativeVelocity.magnitude < minCollisionForce) return;

            var playerNob = collision.gameObject.GetComponentInParent<NetworkObject>();
            if (playerNob == null || !playerNob.IsOwner) return;
            if (HasAuthority) return;

            ClaimOwnershipServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void ClaimOwnershipServerRpc(NetworkConnection sender = null)
        {
            if (sender == null) return;
            if (Time.time - lastOwnershipChangeTime < ownershipCooldown) return;

            GiveOwnership(sender);
            lastOwnershipChangeTime = Time.time;
            SyncOwnershipChangeTimeObserversRpc(lastOwnershipChangeTime);
        }

        [ObserversRpc]
        private void SyncOwnershipChangeTimeObserversRpc(float time)
        {
            lastOwnershipChangeTime = time;
        }

        #endregion

        private void OnDrawGizmos()
        {
            if (rigidbodyToTrack)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawSphere(receivedPosition, 0.1f);
                Gizmos.DrawLine(rigidbodyToTrack.position, receivedPosition);
            }
        }
    }
}
