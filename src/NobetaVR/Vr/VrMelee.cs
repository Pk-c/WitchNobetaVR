using System;
using NobetaVR.Input;
using UnityEngine;
using UnityEngine.XR;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Melee: swing the wand and she swings it.
    ///
    /// Three parts, and they are independent — each is useful without the others.
    ///
    /// **What counts as a swing.** A speed alone is a poor trigger: reaching for a door handle
    /// crosses it for a frame or two, and so does putting the controller down. A distance alone
    /// is worse, since walking across the room covers metres. Taken together they say the thing
    /// that was meant: the hand has to be moving faster than <c>MeleeSpeed</c> *and* keep moving
    /// for <c>MeleeDistance</c> before anything fires. Below the release speed the swing is over
    /// and the next one can start, which is what stops one long sweep from being read as four.
    ///
    /// The hand is measured relative to your own head, and with the head's yaw taken out. That
    /// frame is what makes the test mean "you moved your arm" rather than "your hand moved":
    /// in tracking space, walking a metre moves the hand a metre, and turning on the spot sweeps
    /// a hand held out in front at over a metre a second — both of which are well past any
    /// threshold worth setting, and neither of which is a swing.
    ///
    /// **In the air, the game's own attack.** <c>PlayerInputController.Attack</c>, exactly as
    /// the pad's melee button calls it, because the air attack is not only an attack: the state
    /// it enters holds her up, and attacking again holds her up again. That hang is a movement
    /// option in this game, and it lives entirely inside the state machine, so there is no way
    /// to keep it except by going through the front door. See <see cref="Fire"/>.
    ///
    /// **On the ground, the collision without the animation.** The same call would lock her into
    /// an attack animation that plants her feet and swings the wand for her — on a monitor that
    /// *is* the attack, and in a headset it is the game taking your arm away in the middle of
    /// your own swing. So the ground swing opens the hitbox directly, through the same
    /// <c>OpenAttackCollision</c> the animation events call. Nothing else is given up by doing
    /// it that way: the damage, the element and the knockback are authored on the range object
    /// itself as an <c>AttackData</c>, the hit effects and hit sounds come out of the collision
    /// code, and the swing sound, the voice and the wand trail are the game's own calls, made
    /// here instead of by an animation event. See <see cref="FreeSwing"/>.
    ///
    /// **Where the blow lands.** On the wand, always. The game's melee hitboxes are ordinary
    /// transforms parented under the character — <c>AnimAttackCollision.attackRangeRoot</c> —
    /// that the attack animations switch on and off, and the measured rig says what they are: no
    /// colliders, all twenty-seven at one point, so a range is a position and the blow is a
    /// sphere of <c>g_fCollisionSize</c> about it. That sphere is put on the wand line, the same
    /// origin and direction the shot goes down, so what you hit and what you fire at are one
    /// line and the wand pitch and yaw offsets aim both at once. <c>MeleeHitboxSize</c> is its
    /// radius, and being the only dimension the hitbox has, it is the whole of how forgiving a
    /// swing is. See <see cref="PlaceHitbox"/>.
    ///
    /// They are moved every frame rather than only while a range is open. A hitbox that is
    /// teleported the instant it switches on has, for that one frame, travelled from wherever
    /// the animation left it to where we want it — and a melee test that sweeps between frames
    /// would read that as a blow struck along the whole of that path.
    /// </summary>
    public sealed class VrMelee : MonoBehaviour
    {
        public VrMelee(IntPtr ptr) : base(ptr) { }

        /// <summary>
        /// The hitbox centre this frame, in world space, or null when melee is standing down.
        /// </summary>
        internal static Vector3? HitCentre { get; private set; }

        // -- swing state ---------------------------------------------------------------

        private Vector3 _previous;
        private bool _havePrevious;
        private bool _swinging;
        private bool _fired;
        private float _travelled;
        private Vector3 _heading;
        private float _lastAttackAt = float.NegativeInfinity;
        private int _voice;

        // -- the ranges we have taken over ---------------------------------------------

        private Transform[] _ranges;
        private Vector3[] _rangeLocalPositions;
        private Quaternion[] _rangeLocalRotations;
        private Vector3[] _rangeLocalScales;
        private string _defaultRangeName;
        private AnimAttackCollisionData _data;
        private float _originalCollisionSize;
        private bool _haveOriginalSize;
        private bool _sizeWarned;
        private AnimAttackCollision _collision;
        private IntPtr _boundCollision;
        private bool _displaced;

        private readonly MeleeGizmo _gizmo = new();
        private readonly WandTrail _trail = new();

        private void LateUpdate()
        {
            HitCentre = null;

            var cfg = Plugin.Instance;
            if (cfg == null || !cfg.Melee.Value) { StandDown(); return; }

            var controls = VrControls.Instance;
            if (controls == null || controls.InputController == null) { StandDown(); return; }

            // The same gate the hands use: she is only swung at when she is yours to move. Our
            // own menu is checked separately, since it takes the controllers without the game
            // knowing anything has happened.
            if (!VrHands.PlayerInControl) { StandDown(); return; }
            if (Ui.VrMenu.Instance != null && Ui.VrMenu.Instance.IsOpen) { StandDown(); return; }

            var girl = controls.Camera != null ? controls.Camera.wizardGirl : null;
            if (girl == null) { StandDown(); return; }

            HeadPose.Sample();

            // The one question the whole component branches on. Asked once a frame and passed
            // down, so the hitbox cannot be placed for one answer and the swing fired for the
            // other on a frame where she leaves the ground between the two.
            var free = UseFreeSwing(girl, cfg);

            PlaceHitbox(girl, cfg);
            DetectSwing(controls, girl, cfg, free);
        }

        private void OnDisable() => StandDown();

        /// <summary>
        /// Whether this frame's swing is the mod's own rather than the game's.
        ///
        /// Only on the ground, and only when asked for. Airborne she keeps the game's attack
        /// whatever this setting says, because what is wanted there is not the blow but the
        /// state it puts her in.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private static bool UseFreeSwing(WizardGirlManage girl, Plugin cfg)
            => cfg.MeleeFreeSwingOnGround.Value && Grounded(girl);

        /// <summary>
        /// The game's own footing, not the CharacterController's.
        ///
        /// <c>MoveController.isGrounded</c> is what the state machine acts on, so it is what
        /// "in the air" has to mean here: reading the controller directly would disagree with
        /// the game on exactly the frames that matter, at the top of a step or the lip of a
        /// slope, and disagreeing there means a ground swing on the frame she counts as
        /// airborne — the one case that must not happen, since it is the air attack's hang
        /// that is being protected.
        /// </summary>
        private static bool Grounded(WizardGirlManage girl)
        {
            var move = girl.GetMoveController();
            if (move != null) return move.isGrounded;

            var controller = girl.characterController;
            return controller == null || controller.isGrounded;
        }

        /// <summary>
        /// Hands the ranges back and forgets the swing in progress.
        ///
        /// Both matter. A range left out in front of her would still be there for the cutscene
        /// the mod just stood down for, and a swing left half-accumulated would fire the moment
        /// control came back — from a hand that has been resting on your knee since.
        /// </summary>
        private void StandDown()
        {
            _gizmo.Hide();
            Restore();
            _havePrevious = false;
            _swinging = false;
            _fired = false;
            _travelled = 0f;
        }

        // -- the swing -----------------------------------------------------------------

        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private void DetectSwing(VrControls controls, WizardGirlManage girl, Plugin cfg, bool free)
        {
            // The handle this frame's poll already resolved; see VrInput.TryDevice.
            if (controls.Input == null
             || !controls.Input.TryDevice(XRNode.RightHand, out var device)
             || !InputDevices.TryGetFeatureValue_Vector3f(device.deviceId, "DevicePosition", out var hand))
            {
                _havePrevious = false;
                return;
            }

            // Head-relative, with the head's yaw taken out. See the class comment: this is the
            // difference between measuring your arm and measuring your whole body.
            var sample = Quaternion.Inverse(FlatYaw(HeadPose.Rotation)) * (hand - HeadPose.Raw);

            if (!_havePrevious)
            {
                _havePrevious = true;
                _previous = sample;
                return;
            }

            // Unscaled: your arm keeps moving at its own speed through a hit-stop, and a swing
            // that begins in one is not a slower swing.
            var dt = Time.unscaledDeltaTime;
            var delta = sample - _previous;
            var step = delta.magnitude;
            _previous = sample;
            if (dt <= 0f) return;

            var speed = step / dt;
            var direction = step > 1e-5f ? delta / step : Vector3.zero;

            // A swing also ends when the hand turns round, not only when it stops.
            //
            // This is what was eating hits. Swinging back and forth at an enemy, the hand never
            // slows to the release speed between passes — it is fast at the end of one stroke
            // and fast at the start of the next, with a reversal and no pause in between — so
            // the whole flurry counted as one swing that had already fired, and every stroke
            // after the first landed on nothing. A reversal is the honest end of a stroke, and
            // it is the signal a player is actually giving.
            if (_swinging && direction != Vector3.zero
             && Vector3.Dot(direction, _heading) < ReversalDot)
            {
                EndSwing();
            }

            if (speed >= cfg.MeleeSpeed.Value)
            {
                if (!_swinging)
                {
                    _swinging = true;
                    _fired = false;
                    _travelled = 0f;
                    _heading = direction;
                }
            }
            else if (speed <= cfg.MeleeReleaseSpeed.Value)
            {
                // The other way a swing ends: a hand that has genuinely stopped. The gap
                // between the two speeds is what keeps a single sweep from being chopped into
                // several, since a real stroke slows at both ends of its arc without pausing.
                EndSwing();
                return;
            }

            if (!_swinging) return;

            // The heading follows the arc rather than being fixed at the stroke's first frame,
            // so a wide sweep — which can turn through more than a right angle on its own — is
            // not mistaken for a reversal, while a genuine turn-back still beats it outright.
            if (direction != Vector3.zero)
                _heading = Vector3.Normalize(_heading * (1f - HeadingBlend) + direction * HeadingBlend);

            _travelled += step;
            if (_fired || _travelled < cfg.MeleeDistance.Value) return;
            if (Time.unscaledTime - _lastAttackAt < cfg.MeleeCooldown.Value) return;

            _fired = true;
            _lastAttackAt = Time.unscaledTime;

            Fire(controls, girl, cfg, free);
        }

        /// <summary>How far the hand must turn back for the stroke to count as over: past a
        /// right angle against the heading it has been travelling on.</summary>
        private const float ReversalDot = -0.2f;

        /// <summary>How fast the heading follows the arc, per frame.</summary>
        private const float HeadingBlend = 0.3f;

        private void EndSwing()
        {
            _swinging = false;
            _fired = false;
            _travelled = 0f;
        }

        /// <summary>
        /// One swing, landed the way this frame's footing calls for.
        ///
        /// In the air, the game's own attack. That call is doing two jobs there — the blow and
        /// the hang that holds her up while it plays — and only the state machine can do the
        /// second, so the front door is the only way in. On the ground it is the first job
        /// alone that is wanted, and the animation that comes with it is in the way.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private void Fire(VrControls controls, WizardGirlManage girl, Plugin cfg, bool free)
        {
            if (free && FreeSwing(girl, cfg)) return;

            controls.InputController.Attack();
        }

        /// <summary>
        /// The ground swing: the hitbox, the sound, the voice and the trail, and no animation.
        ///
        /// <c>OpenAttackCollision</c> is the call the attack animations make through an
        /// animation event, so this is the game's own melee arriving by its own path, only
        /// without the pose that would normally carry it. What that path brings with it is
        /// everything that makes a hit read as a hit: the range object carries its own
        /// <c>AttackData</c> — strength, element, knockback — and the collision code raises the
        /// impact effect and the hit sound out of the pools on <c>AnimAttackCollisionData</c>.
        ///
        /// The three calls after it are the ones an attack animation would have fired as
        /// separate events, made here because there is no animation to fire them. They are the
        /// difference between a swing that connects and a swing that feels like one.
        ///
        /// Returns false if there is no range to open, so the caller can fall back to the
        /// game's attack rather than swinging at nothing.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private bool FreeSwing(WizardGirlManage girl, Plugin cfg)
        {
            var range = RangeName(cfg);
            if (string.IsNullOrEmpty(range)) return false;

            // Closed before it is opened, which is the game's own sequence and the second thing
            // that was eating hits. An open collision keeps a list of what it has already
            // touched, so one blow cannot hit the same enemy twice — and that list is cleared
            // when the collision closes, by an animation event, which is precisely what a free
            // swing has not got. Left to itself the window would run on from the previous
            // stroke with the enemy you are hitting already on its list, and the swing would
            // land on nothing for reasons nothing on screen could explain.
            girl.CancelAttackCollision();
            girl.OpenAttackCollision(range);

            var trail = cfg.MeleeTrailSeconds.Value;
            if (trail > 0f) girl.OpenWTrail(trail);

            if (!cfg.MeleeSwingVoice.Value) return true;

            // Round the four the game has, as the combo does. One voice line on every swing
            // would be the same clip over and over at exactly the rate you swing.
            var sound = girl.GetPlayerSound();
            if (sound == null) return true;

            _voice = (_voice + 1) & 3;
            switch (_voice)
            {
                case 0: sound.PlayVoiceAttack01(); break;
                case 1: sound.PlayVoiceAttack02(); break;
                case 2: sound.PlayVoiceAttack03(); break;
                default: sound.PlayVoiceAttack04(); break;
            }

            return true;
        }

        /// <summary>
        /// Which of the character's ranges the ground swing opens.
        ///
        /// Configured if the player has pinned one, and otherwise whichever was picked when the
        /// character was bound. The choice matters for more than geometry: the range carries the
        /// <c>AttackData</c>, so it is also the choice of how hard the blow hits and what it
        /// knocks back.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private string RangeName(Plugin cfg)
        {
            var configured = cfg.MeleeRangeName.Value;
            return string.IsNullOrEmpty(configured) ? _defaultRangeName : configured;
        }

        /// <summary>The rotation's yaw alone, with a fallback for a head looking straight down.</summary>
        private static Quaternion FlatYaw(Quaternion rotation)
        {
            var forward = rotation * Vector3.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 1e-6f)
            {
                // Looking at your feet or at the ceiling: forward flattens to nothing, so the
                // head's up axis carries the yaw instead.
                forward = rotation * Vector3.up;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-6f) return Quaternion.identity;
            }

            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        // -- the hitbox ----------------------------------------------------------------

        /// <summary>
        /// Puts the game's melee ranges on the wand, and keeps them there.
        ///
        /// One answer for both footings. The wand line is the same origin and direction the shot
        /// uses, so what you hit and what you fire at are one line rather than two aimed
        /// separately, and the wand pitch and yaw offsets under Aim point both at once. There is
        /// no second source of truth for where the wand is.
        ///
        /// <c>MeleeHitboxSize</c> multiplies the sphere's radius. Since the measured rig gives a
        /// range no collider and no offset of its own, that radius is the only dimension the
        /// hitbox has — it is not a refinement of how forgiving a swing is, it is the whole of
        /// it. The per-range scale applied below can do nothing on this character and is kept
        /// only for one whose ranges do have colliders, the boss-rush swaps included.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private void PlaceHitbox(WizardGirlManage girl, Plugin cfg)
        {
            var collision = girl.g_AttackCollision;
            if (collision == null) { _gizmo.Hide(); Restore(); _trail.Release(); return; }

            if (!Bind(collision)) { _gizmo.Hide(); return; }

            if (!VrHands.AimOrigin.HasValue) { _gizmo.Hide(); Restore(); _trail.Release(); return; }

            var forward = VrHands.AimDirection;
            var centre = VrHands.AimOrigin.Value + forward * cfg.MeleeHitboxReach.Value;

            // The swing trail rides the same line, for the same reason the hitbox does: there
            // is one wand, and everything that claims to be on it has to come from one place.
            if (cfg.MeleeTrailSeconds.Value > 0f)
                _trail.Follow(girl.transform, VrHands.AimOrigin.Value, forward,
                              cfg.MeleeHitboxReach.Value);
            else
                _trail.Release();

            HitCentre = centre;

            var rotation = forward.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;

            var scale = Mathf.Max(0.01f, cfg.MeleeHitboxSize.Value);

            var data = ResolveData(girl, collision);
            var radius = _haveOriginalSize ? _originalCollisionSize * scale : UnknownRadius;
            if (data != null) data.g_fCollisionSize = _originalCollisionSize * scale;

            if (cfg.MeleeShowHitbox.Value)
                _gizmo.Show(centre, radius, collision.g_bCollisionEnable);
            else
                _gizmo.Hide();

            for (var i = 0; i < _ranges.Length; i++)
            {
                var range = _ranges[i];
                if (range == null) continue;
                range.position = centre;
                range.rotation = rotation;
                range.localScale = _rangeLocalScales[i] * scale;
            }

            _displaced = true;
        }

        /// <summary>
        /// What the gizmo draws when the collision data was never found, in metres. A stated
        /// placeholder rather than a guess dressed as a measurement: it says where the blow
        /// will be, and the log says the radius is not known.
        /// </summary>
        private const float UnknownRadius = 0.5f;

        /// <summary>
        /// Finds the character's collision data, which carries the one number that says how big
        /// a blow is.
        ///
        /// Resolved every frame until it is found rather than once when the character binds.
        /// <c>AnimAttackCollision.g_ACD</c> is empty at bind time — the collision object is
        /// built before the game fills that reference in — so a single read at bind gets null
        /// and keeps it, and the size knob silently does nothing for the rest of the session.
        /// That is exactly what happened. The component itself is in the character's hierarchy
        /// from the start, so it is also looked for there.
        /// </summary>
        private AnimAttackCollisionData ResolveData(WizardGirlManage girl, AnimAttackCollision collision)
        {
            if (_data != null) return _data;

            _data = collision.g_ACD;
            if (_data == null && girl != null)
                _data = girl.GetComponentInChildren<AnimAttackCollisionData>(true);

            if (_data != null)
            {
                _haveOriginalSize = true;
                _originalCollisionSize = _data.g_fCollisionSize;
                Plugin.Log.LogInfo($"melee: collision data on '{_data.name}' — "
                                 + $"size {_originalCollisionSize:F3}, "
                                 + $"time {_data.g_fCollisionTime:F3}s, "
                                 + $"interval {_data.g_fCollisionInterval:F3}s");
                return _data;
            }

            if (!_sizeWarned)
            {
                _sizeWarned = true;
                Plugin.Log.LogWarning("melee: no AnimAttackCollisionData on this character, so "
                                    + "the hitbox size cannot be read or widened. MeleeHitboxSize "
                                    + $"does nothing and the gizmo draws a placeholder {UnknownRadius:F2} m.");
            }

            return null;
        }

        /// <summary>
        /// Collects the range transforms for one character, remembering where each of them
        /// belongs so it can be handed back exactly as it was found.
        ///
        /// Both sources are read. <c>attackRangeRoot</c> is where the ranges live in the
        /// hierarchy, and <c>g_CRList</c> is the list the collision code itself works from;
        /// they are expected to hold the same objects, and a range that is in one and not the
        /// other would be a range either left behind or never moved.
        /// </summary>
        private bool Bind(AnimAttackCollision collision)
        {
            if (collision.Pointer == _boundCollision && _ranges != null) return _ranges.Length > 0;

            // A different character — a skin change, a new stage, the boss rush swapping her
            // out. Whatever we were holding belongs to a body that is no longer there.
            Restore();
            _collision = collision;
            _boundCollision = collision.Pointer;

            var found = new System.Collections.Generic.List<Transform>();

            var root = collision.attackRangeRoot;
            if (root != null)
            {
                for (var i = 0; i < root.childCount; i++)
                {
                    var child = root.GetChild(i);
                    if (child != null) found.Add(child);
                }
            }

            var list = collision.g_CRList;
            if (list != null)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var entry = list[i];
                    if (entry == null) continue;
                    var t = entry.transform;
                    if (t == null || found.Contains(t)) continue;
                    found.Add(t);
                }
            }

            _ranges = found.ToArray();
            _rangeLocalPositions = new Vector3[_ranges.Length];
            _rangeLocalRotations = new Quaternion[_ranges.Length];
            _rangeLocalScales = new Vector3[_ranges.Length];

            for (var i = 0; i < _ranges.Length; i++)
            {
                _rangeLocalPositions[i] = _ranges[i].localPosition;
                _rangeLocalRotations[i] = _ranges[i].localRotation;
                _rangeLocalScales[i] = _ranges[i].localScale;
            }

            // Deliberately not read here: it is still null this early. ResolveData keeps asking.
            _data = null;
            _haveOriginalSize = false;
            _sizeWarned = false;

            _defaultRangeName = PickRange();

            Describe(collision);
            return _ranges.Length > 0;
        }

        /// <summary>
        /// Which range the ground swing opens when the player has not said.
        ///
        /// The first combo step, by name, and the first range otherwise. It is a guess, and it
        /// is a guess with a stated fallback rather than a silent one: the whole list is in the
        /// log with each range's strength beside it, so pinning a different one through
        /// <c>MeleeRangeName</c> is a matter of reading rather than of trying them all.
        /// </summary>
        private string PickRange()
        {
            if (_ranges == null || _ranges.Length == 0) return null;

            foreach (var range in _ranges)
            {
                if (range == null) continue;
                var name = range.name.ToLowerInvariant();
                if (name.Contains("01") || name.EndsWith("1", StringComparison.Ordinal)) return range.name;
            }

            return _ranges[0] != null ? _ranges[0].name : null;
        }

        /// <summary>
        /// Puts the ranges back where the character had them, at the size it had them.
        ///
        /// Not optional. The animation events go on opening and closing these whether the mod is
        /// running or not, so a range abandoned out in front of her is a hitbox hanging in the
        /// air at whatever spot the player last stood — and one abandoned during a cutscene is a
        /// melee that lands from the far side of the room for the rest of the scene.
        /// </summary>
        private void Restore()
        {
            if (!_displaced || _ranges == null) { _displaced = false; return; }

            for (var i = 0; i < _ranges.Length; i++)
            {
                var range = _ranges[i];
                if (range == null) continue;
                range.localPosition = _rangeLocalPositions[i];
                range.localRotation = _rangeLocalRotations[i];
                range.localScale = _rangeLocalScales[i];
            }

            if (_haveOriginalSize && _data != null)
                _data.g_fCollisionSize = _originalCollisionSize;

            _displaced = false;
        }

        /// <summary>
        /// Writes the melee rig out once per character.
        ///
        /// The shape of this rig is the one thing about melee that could not be read off the
        /// interop assemblies: they give the members and say nothing about how many ranges there
        /// are, where they sit, how big they are, or how hard each one hits. It is a screenful,
        /// once, and it is what turns the next round of tuning from guesswork into arithmetic.
        /// </summary>
        private void Describe(AnimAttackCollision collision)
        {
            var root = collision.attackRangeRoot;
            Plugin.Log.LogInfo($"melee: attackRangeRoot '{(root != null ? Path(root) : "none")}', "
                             + $"{_ranges.Length} range(s), hitLayer {collision.hitLayer.value}, "
                             + $"ground swing opens '{_defaultRangeName ?? "nothing"}'");

            for (var i = 0; i < _ranges.Length; i++)
            {
                var range = _ranges[i];
                var shape = Shape(range);

                var attack = range.GetComponent<AttackData>();
                var damage = attack == null
                    ? "no AttackData"
                    : $"strength {attack.g_fStrength:F1}, repulse {attack.g_fRepulse:F1}";

                Plugin.Log.LogInfo($"melee: range '{range.name}' local {range.localPosition.ToString("F3")} "
                                 + $"scale {range.localScale.ToString("F2")} — {shape}, {damage}, "
                                 + $"{(range.gameObject.activeSelf ? "active" : "inactive")}");
            }

            if (_ranges.Length == 0)
                Plugin.Log.LogWarning("melee: no attack ranges found, so the hitbox cannot be "
                                    + "moved and the ground swing has nothing to open. Swings "
                                    + "fall back to the game's own attack, animation and all.");
        }

        /// <summary>
        /// A range's collider, described by its own dimensions rather than by its bounds.
        /// <c>Collider.bounds</c> is world-space and reads as zero on a disabled collider, and
        /// these spend nearly all of their time disabled — so a dump taken between attacks,
        /// which is every dump, would report every range as a point.
        /// </summary>
        private static string Shape(Transform range)
        {
            var collider = range.GetComponent<Collider>();
            if (collider == null) return "no collider";

            var sphere = collider.TryCast<SphereCollider>();
            if (sphere != null) return $"sphere r{sphere.radius:F3}";

            var box = collider.TryCast<BoxCollider>();
            if (box != null) return $"box {box.size.ToString("F3")}";

            var capsule = collider.TryCast<CapsuleCollider>();
            if (capsule != null) return $"capsule r{capsule.radius:F3} h{capsule.height:F3}";

            return collider.GetIl2CppType().Name;
        }

        private static string Path(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
