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
    /// line and the wand pitch and yaw offsets aim both at once. See <see cref="PlaceHitbox"/>.
    ///
    /// They are moved every frame rather than only while a range is open. A hitbox that is
    /// teleported the instant it switches on has, for that one frame, travelled from wherever
    /// the animation left it to where we want it — and a melee test that sweeps between frames
    /// would read that as a blow struck along the whole of that path.
    ///
    /// **A capsule out of one sphere.** A wand is a stick, and a stick's hitbox is a capsule
    /// along it, not a ball somewhere on it. The game cannot be asked for one: the range has no
    /// collider to replace, and the single radius on <c>AnimAttackCollisionData</c> is all the
    /// shape its collision code knows. But the *position* of that sphere is ours every frame,
    /// and a capsule is exactly the set of points within <c>Radius</c> of its axis — so the
    /// sphere is put on the point of the axis nearest whatever is in the capsule. The game's own
    /// test then hits precisely when that thing is inside the capsule, which is the capsule,
    /// evaluated by the game rather than by us. <c>MeleeHitboxLength</c> is the axis, from
    /// <c>Reach - Length/2</c> to <c>Reach + Length/2</c>; zero leaves the plain sphere the game
    /// has. See <see cref="Aim"/>.
    ///
    /// That axis can be tilted off the wand line, and it turns about its own base rather than
    /// about the hand: the near end stays where the reach put it and the far end swings, which
    /// is how a capsule is laid along a sceptre whose angle in her hand is not the angle the
    /// controller points at. The shot is not moved by it — the aim line stays the aim line and
    /// only the volume laid along it turns — so this is the one place the hitbox and the shot
    /// are allowed to disagree, and it is a few degrees of model geometry rather than a second
    /// way to aim. See <see cref="Tilt"/>.
    ///
    /// Every placement is on that axis and every sphere is the same radius, so the union of
    /// them — and of anything a swept test reads between them — is inside the capsule. Aiming
    /// cannot reach past the volume being drawn.
    ///
    /// **Only with the wand out.** The game hides and shows the wand as her animations call for
    /// it, and a live hitbox on an empty hand is a blow struck with nothing. The renderer the
    /// game switches is the gate; see <see cref="WandOut"/>.
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
        private bool? _wandOut;
        private bool _wandWarned;

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

            // An empty hand swings nothing. Standing down here rather than only refusing to
            // fire is the point of the setting: the ranges go back to the character, so there
            // is no hitbox of ours in the world at all while the wand is away.
            if (cfg.MeleeRequireWand.Value && !WandOut(girl)) { StandDown(); return; }

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
        /// The states in which she is off the ground, whatever any footing flag says.
        ///
        /// The state machine is the one reading that cannot be momentarily wrong: it is what
        /// decides which attack a button press turns into, so a swing judged by it can never
        /// disagree with the attack it produces.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<NobetaState> InTheAir = new()
        {
            NobetaState.Air,
            NobetaState.Jump,
            NobetaState.AirAttack,
            NobetaState.AirAim,
            NobetaState.AirChargeShot,
            NobetaState.AirDodge,
            NobetaState.AirDamaged,
            NobetaState.AirDamagedFly,
            NobetaState.AirSlip,
            NobetaState.DamagedFly,
        };

        /// <summary>
        /// The game's own footing.
        ///
        /// <c>MoveController.isGrounded</c> was the first answer and it is the wrong one. It is
        /// a per-frame contact test, and a per-frame contact test says "airborne" for a frame
        /// at the top of every step, on the lip of every slope and in the middle of an ordinary
        /// run — so a swing that happened to fire on one of those frames took the air branch
        /// and came out as the game's own attack, animation and all. That is exactly the fault
        /// the free swing exists to remove, arriving at random on maybe one swing in several,
        /// which is why it read as "free mode does not work" rather than as a footing bug.
        ///
        /// So the state machine is asked instead, and then <c>NobetaRuntimeData.isSky</c>, the
        /// flag the state machine itself runs on. The game maintains that one deliberately —
        /// <c>PlayerController.UpdateSkyState</c> keeps it, with <c>fallTimer</c> beside it —
        /// rather than sampling it, so it reads as "she is in the air" rather than as "nothing
        /// was under her this frame". Both go true the moment she jumps, so the air attack and
        /// the hang it carries are still reached on the first airborne frame.
        ///
        /// If a swing still comes out animated on the ground, <see cref="ReportAnimated"/> puts
        /// all three readings in the log side by side and says which one called it.
        ///
        /// The contact test stays as the last resort, for a character that has neither.
        /// </summary>
        private static bool Grounded(WizardGirlManage girl)
        {
            var controller = girl.playerController;

            if (controller != null && InTheAir.Contains(controller.state)) return false;

            var runtime = controller != null ? controller.runtimeData : null;
            if (runtime != null) return !runtime.isSky;

            var move = girl.GetMoveController();
            if (move != null) return move.isGrounded;

            var character = girl.characterController;
            return character == null || character.isGrounded;
        }

        /// <summary>
        /// Whether the wand is in her hand this frame.
        ///
        /// Read off the renderer the game itself switches — <c>NobetaSkin.weaponMesh</c>, the
        /// mesh on <c>Bone_Weapon</c> under her right hand — rather than inferred from her
        /// state. The state machine says what she is doing; this says what she is holding, and
        /// it is what she is holding that a swing is made with. Both halves of "visible" are
        /// asked, since a prop can be put away by switching the renderer or the object.
        ///
        /// The mod moves that bone onto its own hand while the hands are drawn, so the object
        /// is asked whether it is active in *its* hierarchy, wherever that now is.
        ///
        /// Unknown counts as out. On a character with no weapon renderer to read, refusing
        /// every swing would be a worse answer than allowing them, so the gate opens and the
        /// log says once that it cannot be trusted here.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private bool WandOut(WizardGirlManage girl)
        {
            var renderer = WandRenderer(girl);
            if (renderer == null)
            {
                if (!_wandWarned)
                {
                    _wandWarned = true;
                    Plugin.Log.LogWarning("melee: no weapon renderer on this character, so "
                                        + "MeleeRequireWand cannot tell a drawn wand from a "
                                        + "stowed one. Swings are allowed either way.");
                }
                return true;
            }

            var visible = renderer.enabled && renderer.gameObject.activeInHierarchy;

            // Logged on the change alone. If swings stop landing, the one thing worth knowing
            // is whether this ever said "out" — which separates a gate reading the wrong thing
            // from a wand that really is away.
            if (_wandOut != visible)
            {
                _wandOut = visible;
                Plugin.Log.LogInfo(visible ? "melee: wand out" : "melee: wand stowed");
            }

            return visible;
        }

        /// <summary>
        /// The wand's renderer, from the skin if it has one and from the props the hands are
        /// carrying otherwise — which is the same object by another route, since
        /// <see cref="DetachedHands"/> took them off <c>Bone_Weapon</c> itself.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private static Renderer WandRenderer(WizardGirlManage girl)
        {
            var skin = girl.skinInstance;
            var mesh = skin != null ? skin.weaponMesh : null;
            if (mesh != null) return mesh;

            var props = VrHands.WandProps;
            if (props == null) return null;

            foreach (var prop in props)
            {
                if (prop == null) continue;
                var renderer = prop.GetComponentInChildren<Renderer>(true);
                if (renderer != null) return renderer;
            }

            return null;
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

            ReportAnimated(girl, cfg, free);
            controls.InputController.Attack();
        }

        /// <summary>
        /// Says, the first few times it happens, why a swing came out animated while the free
        /// swing was asked for.
        ///
        /// There are only two ways it can: she was judged airborne, or there was no range to
        /// open. From inside a headset the two are one symptom — she plays the attack and takes
        /// your arm with her — and nothing else in the log distinguishes them. Bounded, because
        /// this fires on a swing and swings come in flurries.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private void ReportAnimated(WizardGirlManage girl, Plugin cfg, bool free)
        {
            if (!cfg.MeleeFreeSwingOnGround.Value) return;
            if (_animatedReported >= AnimatedReportLimit) return;
            _animatedReported++;

            var controller = girl.playerController;
            var runtime = controller != null ? controller.runtimeData : null;
            var move = girl.GetMoveController();

            Plugin.Log.LogInfo(
                $"melee: swing came out animated — "
              + $"{(free ? $"no range to open (range '{RangeName(cfg) ?? "none"}')" : "she was in the air")}"
              + $" [state {(controller != null ? controller.state.ToString() : "<none>")}, "
              + $"isSky {(runtime != null ? runtime.isSky.ToString() : "<none>")}, "
              + $"isGrounded {(move != null ? move.isGrounded.ToString() : "<none>")}]");
        }

        private int _animatedReported;

        /// <summary>How many animated swings the log will explain before it stops.</summary>
        private const int AnimatedReportLimit = 8;

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
        /// <c>MeleeHitboxRadius</c> is written straight into <c>g_fCollisionSize</c> rather than
        /// scaling it, because on the measured rig a range has no collider and no offset of its
        /// own: that radius is the only dimension the game's melee has, so a number in metres
        /// says what the blow is where a multiplier only says what it used to be. The per-range
        /// scale applied below can do nothing on this character and is kept, in the same ratio,
        /// for one whose ranges do have colliders — the boss-rush swaps included.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private void PlaceHitbox(WizardGirlManage girl, Plugin cfg)
        {
            var collision = girl.g_AttackCollision;
            if (collision == null) { _gizmo.Hide(); Restore(); _trail.Release(); return; }

            if (!Bind(collision)) { _gizmo.Hide(); return; }

            if (!VrHands.AimOrigin.HasValue) { _gizmo.Hide(); Restore(); _trail.Release(); return; }

            var forward = VrHands.AimDirection;
            var line = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;

            // The swing trail rides the wand line itself, for the same reason the hitbox hangs
            // off it: there is one wand, and everything that claims to be on it has to come
            // from one place. The tilt below is the hitbox's own and stops here.
            if (cfg.MeleeTrailSeconds.Value > 0f)
                _trail.Follow(girl.transform, VrHands.AimOrigin.Value, forward,
                              cfg.MeleeHitboxReach.Value);
            else
                _trail.Release();

            var radius = Mathf.Max(0.01f, cfg.MeleeHitboxRadius.Value);
            var half = Mathf.Max(0f, cfg.MeleeHitboxLength.Value) * 0.5f;

            // The base first, on the wand line, because it is what the tilt turns about: the
            // reach then means the same thing at every angle, and tuning the two against each
            // other does not turn into chasing one with the other.
            var from = VrHands.AimOrigin.Value + line * (cfg.MeleeHitboxReach.Value - half);
            var axis = Tilt(line, cfg.MeleeHitboxPitch.Value, cfg.MeleeHitboxYaw.Value);
            var to = from + axis * (half * 2f);
            var centre = (from + to) * 0.5f;

            var rotation = Facing(axis);

            var data = ResolveData(girl, collision);
            if (data != null) data.g_fCollisionSize = radius;

            // The range's own scale, in the ratio the radius was changed by. It does nothing
            // here — these ranges have no colliders — and it is what would carry the change on a
            // character whose ranges do.
            var scale = _haveOriginalSize && _originalCollisionSize > 1e-4f
                ? radius / _originalCollisionSize
                : 1f;

            // The one point the game will test, chosen so that its sphere answers the capsule.
            var point = half > 1e-4f ? Aim(collision, girl, from, to, radius) : centre;
            HitCentre = point;

            if (cfg.MeleeShowHitbox.Value)
                _gizmo.Show(from, to, _haveOriginalSize ? radius : UnknownRadius,
                            collision.g_bCollisionEnable);
            else
                _gizmo.Hide();

            for (var i = 0; i < _ranges.Length; i++)
            {
                var range = _ranges[i];
                if (range == null) continue;
                range.position = point;
                range.rotation = rotation;
                range.localScale = _rangeLocalScales[i] * scale;
            }

            _displaced = true;
        }

        /// <summary>
        /// Which point on the capsule's axis the game's single sphere is put on this frame.
        ///
        /// The capsule is asked for by hand — <c>Physics.OverlapCapsule</c> against the same
        /// <c>hitLayer</c> the game's own melee tests, so the question is the game's question —
        /// and whatever it finds, the sphere is moved to the point of the axis nearest that
        /// thing. Being within <c>radius</c> of the axis *is* being in the capsule, so the
        /// game's test on that point answers exactly what a capsule collider would have, and
        /// answers it in the game's own collision code, with its own exclusions, its own hit
        /// effects and its own damage.
        ///
        /// Nothing inside means nothing to aim at, and the sphere sits at the middle of the
        /// axis: no target, no difference from the plain sphere.
        ///
        /// What is in there is ranked before it is measured, because the layer mask lets scenery
        /// in and a sphere pulled onto a wall while an enemy stands at the other end of the wand
        /// is a swing spent on a spark. The game's own tags do the ranking: <c>Enemy</c> first,
        /// then <c>AttackableObject</c> — the breakables, which are a real target and not
        /// scenery — then everything else, which is still allowed to be hit because the game
        /// hits it too. Within a tier the nearer to the axis wins.
        ///
        /// Only one target can be served per test: the game tests one point, and that is the
        /// price of using its collision rather than writing a second one.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private Vector3 Aim(AnimAttackCollision collision, WizardGirlManage girl,
                            Vector3 from, Vector3 to, float radius)
        {
            var centre = (from + to) * 0.5f;

            var count = Physics.OverlapCapsuleNonAlloc(from, to, radius, _candidates,
                                                       collision.hitLayer.value,
                                                       QueryTriggerInteraction.Collide);
            if (count <= 0) return centre;

            var best = centre;
            var bestScore = float.PositiveInfinity;
            Collider bestCollider = null;

            for (var i = 0; i < count && i < _candidates.Length; i++)
            {
                var candidate = _candidates[i];
                if (candidate == null) continue;

                var t = candidate.transform;
                if (t == null) continue;

                // Herself. The game's collision would ignore her anyway — it has g_IgnoreTag
                // for exactly that — but a sphere aimed at her own body is a sphere not aimed
                // at whatever you swung at.
                if (girl != null && girl.transform != null && t.IsChildOf(girl.transform)) continue;
                if (t.CompareTag("Player")) continue;

                // Two steps, because either alone picks a worse point: the collider's nearest
                // point to the axis, then the axis's nearest point to that.
                var bounds = candidate.bounds;
                var near = bounds.ClosestPoint(Nearest(from, to, bounds.center));
                var onAxis = Nearest(from, to, near);

                var score = (near - onAxis).magnitude + Tier(t) * TierStep;

                if (score >= bestScore) continue;
                bestScore = score;
                best = onAxis;
                bestCollider = candidate;
            }

            // Once per kind of thing, up to a handful. One line said what the capsule found
            // first and nothing about the rest, and the rest is the question: it was a line
            // like this, naming a barrel tagged AttackableObject, that showed the ranking had
            // to be more than "Enemy or not".
            if (bestCollider != null && _reportedTags.Count < ReportedTagLimit)
            {
                var tag = bestCollider.tag;
                if (_reportedTags.Add(tag))
                {
                    Plugin.Log.LogInfo($"melee: capsule aimed at '{bestCollider.name}' "
                                     + $"(tag {tag}, layer {bestCollider.gameObject.layer}), "
                                     + $"{Vector3.Distance(from, best):F2} m along the axis");
                }
            }

            return best;
        }

        /// <summary>
        /// What a thing in the capsule is worth being hit, lowest first. The tags are the
        /// game's own, read out of its tag list rather than guessed: <c>Enemy</c> is what its
        /// enemies carry and <c>AttackableObject</c> is what its breakables carry.
        /// </summary>
        private static int Tier(Transform t)
        {
            if (t.CompareTag("Enemy")) return 0;
            if (t.CompareTag("AttackableObject")) return 1;
            return 2;
        }

        /// <summary>
        /// How far behind a better target each tier is put. Larger than any distance the capsule
        /// can produce on its own — no point inside it is further than one radius from the
        /// axis, and a radius is a fraction of a metre — so an enemy anywhere inside beats a
        /// barrel anywhere inside, and a barrel beats a wall.
        /// </summary>
        private const float TierStep = 100f;

        /// <summary>How many kinds of target the log will name before it stops.</summary>
        private const int ReportedTagLimit = 8;

        private readonly System.Collections.Generic.HashSet<string> _reportedTags = new();

        /// <summary>How many things the capsule will consider in one frame.</summary>
        private const int CandidateLimit = 16;

        private readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider>
            _candidates = new(CandidateLimit);

        /// <summary>
        /// The wand line, turned by the hitbox's own pitch and yaw.
        ///
        /// In the line's own frame rather than the world's, so pitch stays "towards her feet"
        /// and yaw "across the swing" however she is standing: an offset set once while looking
        /// at the sceptre has to keep meaning the same thing when you turn round.
        /// </summary>
        private static Vector3 Tilt(Vector3 line, float pitch, float yaw)
        {
            if (Mathf.Abs(pitch) < 0.01f && Mathf.Abs(yaw) < 0.01f) return line;
            return Facing(line) * Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
        }

        /// <summary>
        /// A rotation looking along <paramref name="direction"/>, with an up axis that is never
        /// parallel to it. A wand can be pointed at the ceiling, and <c>LookRotation</c> answers
        /// that with an error and the identity — which would drop the hitbox back onto the
        /// world's forward at the one moment you are looking straight along it.
        /// </summary>
        private static Quaternion Facing(Vector3 direction)
        {
            var up = Mathf.Abs(direction.y) > 0.999f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(direction, up);
        }

        /// <summary>The point of the segment a-b nearest <paramref name="point"/>.</summary>
        private static Vector3 Nearest(Vector3 a, Vector3 b, Vector3 point)
        {
            var span = b - a;
            var length = span.sqrMagnitude;
            if (length < 1e-8f) return a;

            var t = Mathf.Clamp01(Vector3.Dot(point - a, span) / length);
            return a + span * t;
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
                                    + "the hitbox radius can be neither read nor set. "
                                    + "MeleeHitboxRadius does nothing, the blow keeps whatever "
                                    + "radius the character came with, the capsule is aimed at "
                                    + "the radius you asked for rather than the one being "
                                    + $"tested, and the gizmo draws a placeholder {UnknownRadius:F2} m.");
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
