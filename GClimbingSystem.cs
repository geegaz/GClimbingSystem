using System;
using UdonSharp;
using UnityEngine;
using UnityEngine.Serialization;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

namespace VRC_GClimbing
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GClimbingSystem : UdonSharpBehaviour
    {
        [SerializeField] private Transform handTransform;
        [SerializeField] private LayerMask climableMask;
        [SerializeField] private Material climbingHighlightMaterial;

        [Header("Walljump")] [Space] 
        [SerializeField] private bool wallJumpEnabled = true;
        [SerializeField] private float wallJumpSpeed = 5f;

        [Header("Climbing")] [Space]
        [Tooltip("Sets the player's gravity strength to 0 while climbing, helps prevent jittering")]
        [SerializeField] private bool overrideGravity = true;

        [Tooltip("Makes an average of the previous velocities to fake the conservation of force when letting go")]
        [SerializeField] private bool velocityBufferEnabled = true;

        [Tooltip("Teleports the player at the grabbing point if the grabbed surface is facing up and there's enough space around")]
        [SerializeField] private bool ledgeHelpEnabled = true;
        [SerializeField] private LayerMask ledgeHelpMask;
        [SerializeField] private float ledgeHelpMaxAngle = 35f;
        [SerializeField] private float ledgeHelpCapsuleHeight = 2f;
        [SerializeField] private float ledgeHelpCapsuleRadius = 0.1f;
        [SerializeField] private float ledgeHelpCapsuleMargin = 0.01f;

        [Header("VR Settings")] 
        [Tooltip("Use the grip buttons instead of the triggers to climb")]
        [SerializeField] private bool useGripButtons = true;
        [SerializeField] private float handRadius = 0.1f;
        [SerializeField] private float maxFlingSpeed = 6f;
        [SerializeField] private float flingSpeedMultiplier = 1.2f;

        [Header("Desktop Settings")] 
        [SerializeField] private float headReach = 2f;
        [SerializeField] private float headDistance = 0.5f;
        [SerializeField] private float headMoveSpeed = 5f;

        [Header("Events")] 
        [SerializeField] private bool sendEventsToClimbedObjects = true;
        [SerializeField] private UdonBehaviour[] eventTargets;
        [SerializeField] private string grabbedEventName = "ClimbingGrabbed";
        [SerializeField] private string droppedEventName = "ClimbingDropped";

        private readonly Collider[] _grabSurfaces = new Collider[1];
        private readonly Vector3[] _velocityBuffer = new Vector3[5];

        private bool _holdingMouseLeft;
        private Transform _lastClimbedTransform;

        // Climbing & velocity
        private Vector3 _lastClimbedVelocity;

        // Desktop-specific
        private Vector3 _lastHeadDir;
        private float _lastHeadDistance;
        private Vector3 _lastTransformPosition;
        private Vector3 _lastTransformVelocity;

        private float _leftHandHighlight;
        private float _rightHandHighlight;

        [NonSerialized] public bool Climbing;
        [NonSerialized] public HandType ClimbingHand;
        [NonSerialized] public bool InVR;

        // Cache local player and VR status
        [NonSerialized] public VRCPlayerApi LocalPlayer;


        #region Events

        private void Start()
        {
            LocalPlayer = Networking.LocalPlayer;
            if (LocalPlayer != null)
                InVR = LocalPlayer.IsUserInVR();
            if (handTransform)
                handTransform.localScale = Vector3.one * handRadius;
        }

        private void Update()
        {
            if (climbingHighlightMaterial) UpdateMaterial();
        }

        public override void PostLateUpdate()
        {
            if (Climbing) UpdateGrab(ClimbingHand);
        }

        #endregion

        #region Inputs

        public override void InputJump(bool value, UdonInputEventArgs args)
        {
            if (value && wallJumpEnabled && Climbing)
                // Let go with jump force
                DropWithBoost(Vector3.up * wallJumpSpeed);
        }

        public override void InputUse(bool value, UdonInputEventArgs args)
        {
            if (InVR)
            {
                if (useGripButtons) return; // Skip execution
                ProcessInput(value, args.handType);
            }
            else
            {
                // The Use input is always Left Click on Desktop
                ProcessInput(value, HandType.LEFT);
            }
        }

        public override void InputGrab(bool value, UdonInputEventArgs args)
        {
            if (InVR)
            {
                if (!useGripButtons) return; // Skip execution
                ProcessInput(value, args.handType);
            }
        }

        public override void InputDrop(bool value, UdonInputEventArgs args)
        {
            if (InVR)
                Debug.LogError("Climbing system detected InputDrop event in VR - this is not handled");
            else
                // The Drop input is always Right Click on PC
                ProcessInput(value, HandType.RIGHT);
        }


        private void ProcessInput(bool value, HandType hand)
        {
            if (InVR)
            {
                if (value && !IsClimbingWithHand(hand) && TestGrabVR(hand)) Grab(hand);
                if (!value && IsClimbingWithHand(hand))
                {
                    if (ledgeHelpEnabled && TestLedgeHelp(out var pos))
                        DropWithTeleport(pos);
                    else
                        Drop();
                }
            }
            else // Desktop
            {
                // HandType is used to identify click
                if (hand == HandType.LEFT)
                {
                    if (value && TestGrabDesktop())
                    {
                        _holdingMouseLeft = true; // Don't change the head distance
                        Grab(HandType.RIGHT); // Use the right hand by default
                    }
                    else
                    {
                        _holdingMouseLeft = false;
                        if (Climbing)
                        {
                            if (LocalPlayer.IsPlayerGrounded())
                                Drop(true);
                            else if (ledgeHelpEnabled && TestLedgeHelp(out var pos))
                                DropWithTeleport(pos);
                        }
                    }
                }
                else if (!value && Climbing && !HasPickupInHand(VRC_Pickup.PickupHand.Right))
                {
                    Drop(); // Drop on right click if the player doesn't have a pickup
                }
            }
        }

        #endregion

        #region Climbing

        private void UpdateMaterial()
        {
            if (InVR)
            {
                // Update from tracked hand position
                GetHandPos(HandType.RIGHT, out var rightHandPos);
                GetHandPos(HandType.LEFT, out var leftHandPos);

                _rightHandHighlight = Mathf.Lerp(_rightHandHighlight,
                    Climbing && ClimbingHand == HandType.RIGHT ? 0f : 0.5f, 0.2f);
                _leftHandHighlight = Mathf.Lerp(_leftHandHighlight,
                    Climbing && ClimbingHand == HandType.LEFT ? 0f : 0.5f, 0.2f);

                climbingHighlightMaterial.SetFloat("_RightHandDist",
                    Climbing && ClimbingHand == HandType.RIGHT ? 0f : handRadius);
                climbingHighlightMaterial.SetFloat("_LeftHandDist",
                    Climbing && ClimbingHand == HandType.LEFT ? 0f : handRadius);
                climbingHighlightMaterial.SetVector("_RightHandPosition",
                    new Vector4(rightHandPos.x, rightHandPos.y, rightHandPos.z, _rightHandHighlight));
                climbingHighlightMaterial.SetVector("_LeftHandPosition",
                    new Vector4(leftHandPos.x, leftHandPos.y, leftHandPos.z, _leftHandHighlight));
            }
            else
            {
                // Update from raycast
                GetHeadPos(out var headPos, out var headDir);

                Vector3 targetPos;
                if (Physics.Raycast(headPos, headDir, out var hit, headReach, climableMask,
                        QueryTriggerInteraction.Collide))
                {
                    targetPos = hit.point;
                    climbingHighlightMaterial.SetFloat("_RightHandDist",
                        Climbing && _holdingMouseLeft ? 0f : handRadius);
                }
                else
                {
                    targetPos = headPos + headDir * headReach;
                    climbingHighlightMaterial.SetFloat("_RightHandDist", 0f);
                }

                _rightHandHighlight = Mathf.Lerp(_rightHandHighlight, Climbing && _holdingMouseLeft ? 0f : 0.5f, 0.2f);
                climbingHighlightMaterial.SetVector("_RightHandPosition",
                    new Vector4(targetPos.x, targetPos.y, targetPos.z, _rightHandHighlight));
            }
        }

        private void UpdateGrab(HandType hand)
        {
            Vector3 climbingPos;
            if (InVR)
            {
                GetHandPos(hand, out climbingPos);
            }
            else
            {
                GetHeadPos(out var headPos, out var headDir);

                if (_holdingMouseLeft) // Update head offset based on mouse direction while left button is pressed
                    _lastHeadDir = headDir;
                if (_lastHeadDistance > headDistance) // Smoothly move to the target position
                    _lastHeadDistance =
                        Mathf.MoveTowards(_lastHeadDistance, headDistance, headMoveSpeed * Time.deltaTime);

                climbingPos = headPos + _lastHeadDir * _lastHeadDistance;
            }

            // Calculate climbing velocity (total velocity to reach the grabbing point)
            var climbingOffset = handTransform.position - climbingPos;
            _lastClimbedVelocity = climbingOffset * (1.0f / Time.deltaTime);

            // Calculate transform velocity (velocity of the object we're grabbing on)
            var transformOffset = handTransform.position - _lastTransformPosition;
            _lastTransformVelocity = transformOffset * (1.0f / Time.deltaTime);
            _lastTransformPosition = handTransform.position;

            // Store velocity in buffer for smoothing on drop
            if (velocityBufferEnabled)
                _velocityBuffer[Time.frameCount % _velocityBuffer.Length] = _lastClimbedVelocity;

            // Apply velocity
            LocalPlayer.SetVelocity(_lastClimbedVelocity);
        }

        #region Climbing Actions

        public void Grab(HandType hand)
        {
            // Reset last velocity
            _lastClimbedVelocity = Vector3.zero;
            _lastTransformVelocity = Vector3.zero;
            _lastTransformPosition = handTransform.position;

            // Override gravity
            if (overrideGravity)
                LocalPlayer.SetGravityStrength(0f);

            // Send events
            if (Climbing)
                SendDroppedEvent(_lastClimbedTransform.gameObject); // previous climbed object
            SendGrabbedEvent(handTransform.parent.gameObject); // current climbed object

            ClimbingHand = hand;
            Climbing = true;
        }

        public void ForceGrab(Transform tf, HandType hand, Vector3 offset)
        {
            handTransform.position = tf.position + offset;
            handTransform.parent = tf;

            Grab(hand);
        }

        public void Drop(bool resetVelocity = false)
        {
            if (resetVelocity)
            {
                LocalPlayer.SetVelocity(Vector3.zero);
            }
            else
            {
                // Velocity buffering
                if (velocityBufferEnabled)
                {
                    // Get the average of the climbing velocity 
                    // during the last few frames
                    var vel = Vector3.zero;
                    for (var i = 0; i < _velocityBuffer.Length; i++) vel += _velocityBuffer[i];
                    _lastClimbedVelocity = vel / _velocityBuffer.Length;
                }

                // Clamp climbing velocity to something more acceptable,
                // to avoid people getting flung too far when they get stuck under a ceiling or against a wall.
                // Transform velocity is not affected, to allow moving objects to fling you far when climbed
                _lastClimbedVelocity = _lastTransformVelocity + Vector3.ClampMagnitude(
                    _lastClimbedVelocity - _lastTransformVelocity, maxFlingSpeed) * flingSpeedMultiplier;
                LocalPlayer.SetVelocity(_lastClimbedVelocity);
            }

            // Reset gravity
            if (overrideGravity)
                LocalPlayer.SetGravityStrength();

            // Send events
            SendDroppedEvent(handTransform.parent.gameObject);

            Climbing = false;
        }

        public void DropWithBoost(Vector3 boost)
        {
            Drop();
            LocalPlayer.SetVelocity(_lastClimbedVelocity + boost);
        }

        public void DropWithTeleport(Vector3 pos)
        {
            Drop(true);
            LocalPlayer.TeleportTo(pos, LocalPlayer.GetRotation(), VRC_SceneDescriptor.SpawnOrientation.Default, true);
        }

        public void DropGrabbed(Transform tf)
        {
            if (IsGrabbing(tf)) Drop();
        }

        #endregion

        #region Climbing Tests

        private bool TestGrabVR(HandType hand)
        {
            GetHandPos(hand, out var handPos);

            if (Physics.OverlapSphereNonAlloc(handPos, handRadius, _grabSurfaces, climableMask,
                    QueryTriggerInteraction.Collide) >= 1)
            {
                // Store previous transform to send let go events
                _lastClimbedTransform = handTransform.parent;
                // Reparent hand transform to new parent
                handTransform.position = handPos; // Don't move hand in VR
                handTransform.parent = _grabSurfaces[0].transform;
                return true;
            }

            return false;
        }

        private bool TestGrabDesktop()
        {
            GetHeadPos(out var headPos, out var headDir);

            if (Physics.Raycast(headPos, headDir, out var hit, headReach, climableMask,
                    QueryTriggerInteraction.Collide))
            {
                // Store previous transform to send let go events
                _lastClimbedTransform = handTransform.parent;
                // Reparent hand transform to new parent
                handTransform.position = hit.point;
                handTransform.parent = hit.transform;
                // Store head distance to move smoothly there
                _lastHeadDistance = Vector3.Distance(headPos, hit.point);
                return true;
            }

            return false;
        }

        private bool TestLedgeHelp(out Vector3 teleportPos)
        {
            GetHeadPos(out var headPos, out var headDir);
            var handVec = _lastTransformPosition - headPos;

            if (Physics.Raycast(headPos, handVec.normalized, out var hit, handVec.magnitude + 1f, ledgeHelpMask,
                    QueryTriggerInteraction.Ignore))
            {
                var facingUp = Vector3.Angle(hit.normal, Vector3.up) < ledgeHelpMaxAngle;
                // Check if the surface is facing up and there's enough space to teleport the player there
                // Might have some issues with curved surfaces since we're checking a capsule,
                // increase the margin or lower the max angle if that happens too often
                if (facingUp && !Physics.CheckCapsule(
                        hit.point + Vector3.up * (ledgeHelpCapsuleRadius + ledgeHelpCapsuleMargin),
                        hit.point + Vector3.up *
                        (ledgeHelpCapsuleHeight + ledgeHelpCapsuleRadius + ledgeHelpCapsuleMargin),
                        ledgeHelpCapsuleRadius - ledgeHelpCapsuleMargin,
                        ledgeHelpMask, QueryTriggerInteraction.Ignore))
                {
                    teleportPos = hit.point;
                    return true;
                }
            }

            teleportPos = Vector3.zero;
            return false;
        }

        #endregion

        #region Climbing Events

        private void SendGrabbedEvent(GameObject climbed_object)
        {
            if (sendEventsToClimbedObjects)
            {
                var behavior = (UdonBehaviour)climbed_object.GetComponent(typeof(UdonBehaviour));
                if (behavior)
                    behavior.SendCustomEvent(grabbedEventName);
            }

            foreach (var target in eventTargets) target.SendCustomEvent(grabbedEventName);
        }

        private void SendDroppedEvent(GameObject climbed_object)
        {
            if (sendEventsToClimbedObjects)
            {
                var behavior = (UdonBehaviour)climbed_object.GetComponent(typeof(UdonBehaviour));
                if (behavior)
                    behavior.SendCustomEvent(droppedEventName);
            }

            foreach (var target in eventTargets) target.SendCustomEvent(droppedEventName);
        }

        #endregion

        #region Climbing Utilities

        public bool IsGrabbing(Transform tf)
        {
            if (Climbing)
                return handTransform.parent == tf;
            return false;
        }

        public bool HasPickupInHand(VRC_Pickup.PickupHand hand)
        {
            return LocalPlayer.GetPickupInHand(hand) != null;
        }

        public bool IsClimbingWithHand(HandType hand)
        {
            return Climbing && ClimbingHand == hand;
        }

        private void GetHandPos(HandType hand, out Vector3 hand_pos)
        {
            var handTrackingData = hand == HandType.LEFT
                ? LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand)
                : LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand);
            hand_pos = handTrackingData.position;
        }

        private void GetHeadPos(out Vector3 head_pos, out Vector3 head_dir)
        {
            var headTrackingData = LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            head_pos = headTrackingData.position;
            head_dir = headTrackingData.rotation * Vector3.forward;
        }

        #endregion

        #endregion
    }
}