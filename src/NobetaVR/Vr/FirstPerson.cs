using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Moves the viewpoint from the end of the game's camera boom to Nobeta's head, and takes
    /// off the things a third-person camera does that a head must not.
    ///
    /// The game's camera is left running throughout. Its yaw is still the player's look
    /// direction, still driven by the stick, and everything downstream of it — cutscene modes,
    /// aiming, lock-on — keeps working. What changes is where the view sits and which parts of
    /// the game's framing survive.
    /// </summary>
    internal sealed class FirstPerson
    {
        /// <summary>
        /// Where the eyes sit relative to the head bone, in metres along the view.
        ///
        /// Model geometry rather than preference, which is why these are numbers here and
        /// not settings: the bone is at the base of the skull on this rig, and how far in
        /// front of and above it a pair of eyes goes is a fact about the mesh that every
        /// player shares. What does vary is a player's own comfort offset, and that is
        /// HeadOffsetX/Y/Z, added on top of these.
        /// </summary>
        private const float EyeForward = 0.10f;
        private const float EyeUp = 0.05f;

        private PlayerCamera _playerCamera;
        private Transform _head;

        /// <summary>
        /// The head bone the view is anchored to, or null before it resolves. Exposed because
        /// the hands must be placed relative to her actual head, not to the camera.
        /// </summary>
        public Transform HeadBone => _head;
        private Vector3 _headScale = Vector3.one;
        private bool _headScaleSaved;
        private bool _comfortApplied;

        /// <summary>Forgets everything tied to one PlayerCamera instance; call when it changes.</summary>
        public void Rebind(PlayerCamera playerCamera)
        {
            RestoreHead();
            _playerCamera = playerCamera;
            _head = null;
            _headScaleSaved = false;
            _comfortApplied = false;
            _eyeSeeded = false;
            _candidatesReported = false;

            // A new stage is a new spawn, so the view goes back to riding her facing until she
            // is the player's again — and what the player did in the stage before is not an
            // answer to whether they have asked for anything in this one.
            _viewIsYours = false;
            _riding = false;
            _lastHandle = float.NaN;
            _atRestFor = 0;
            Input.VrControls.ForgetPlayerAction();
        }

        /// <summary>
        /// Finds the bone the view sits on.
        ///
        /// <c>NobetaIKController.head</c> is the obvious candidate and the wrong one: on this
        /// rig it resolves to a transform called <c>HeadDirect</c>, a look-at helper rather than
        /// the skull. Anchoring there put the camera inside a head that was never hidden — the
        /// scale-to-nothing landed on the helper, which has no mesh — so the view filled with
        /// the inside of Nobeta's face. The facing diagnostic ruled out the other explanation:
        /// camera and body agreed to within 13 degrees, so nothing was ever turned around.
        ///
        /// So the skeleton is searched instead, and the helper is kept only as a starting point
        /// for finding the character root. Candidates are logged the first time, because a bone
        /// name is a fact about the model that no amount of reasoning will produce.
        ///
        /// Re-resolved while null, since the skin loads asynchronously: on the opening frames of
        /// a stage the chain exists but ends in nothing.
        /// </summary>
        private Transform Head()
        {
            if (_head != null) return _head;
            if (_playerCamera == null) return null;

            var girl = _playerCamera.wizardGirl;
            var skin = girl != null ? girl.skinController : null;
            var ik = skin != null ? skin.ik : null;
            var helper = ik != null ? ik.head : null;
            if (helper == null) return null;

            var root = girl != null ? girl.transform : helper.root;
            _head = FindHeadBone(root, helper);

            if (_head != null)
            {
                Plugin.Log.LogInfo($"first person anchored to '{Path(_head)}' "
                                 + $"(IK helper was '{helper.name}')");
            }
            return _head;
        }

        private static readonly string[] NotABone = { "direct", "target", "look", "aim", "end", "ik" };

        /// <summary>
        /// Picks the head bone out of the skeleton by name, preferring an exact "Head" and
        /// rejecting the helpers that merely contain the word.
        /// </summary>
        private Transform FindHeadBone(Transform root, Transform fallback)
        {
            var wanted = Plugin.Instance.HeadBoneName.Value;
            Transform exact = null, partial = null;
            var candidates = new System.Collections.Generic.List<string>();

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var name = t.name;
                if (name.IndexOf("head", System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                candidates.Add(name);

                if (!string.IsNullOrEmpty(wanted))
                {
                    if (name == wanted) return t;
                    continue;
                }

                var lower = name.ToLowerInvariant();
                var helperish = false;
                foreach (var bad in NotABone)
                    if (lower.Contains(bad)) { helperish = true; break; }
                if (helperish) continue;

                if (lower == "head") exact = t;
                else partial ??= t;
            }

            if (!_candidatesReported)
            {
                _candidatesReported = true;
                Plugin.Log.LogInfo($"head-bone candidates under '{root.name}': "
                                 + (candidates.Count > 0 ? string.Join(", ", candidates) : "none"));
                if (!string.IsNullOrEmpty(wanted))
                    Plugin.Log.LogInfo($"HeadBoneName is set to '{wanted}'");
            }

            var chosen = exact ?? partial;
            if (chosen == null)
            {
                Plugin.Log.LogWarning($"No head bone found; falling back to the IK helper "
                                    + $"'{fallback.name}', which will put the view inside her head.");
                return fallback;
            }
            return chosen;
        }

        private bool _candidatesReported;

        private static string Path(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        /// <summary>
        /// Where the view should sit and which way it should face, before the headset's own
        /// pose is added on top.
        ///
        /// Returns false when the head bone is not available yet, in which case the caller
        /// keeps the game's own camera pose and the view simply stays in third person for a
        /// few frames rather than snapping somewhere wrong.
        /// </summary>
        public bool GetOrigin(Quaternion gameCameraRotation, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;

            var head = Head();
            if (head == null) return false;

            ApplyComfort();

            var cfg = Plugin.Instance;

            // Yaw from the game, pitch and roll from your neck.
            //
            // Taking the game camera's full rotation would add its pitch to the headset's, so
            // looking up would pitch twice and the horizon would tilt with every camera shake.
            // Yaw alone keeps stick turning working while leaving the other two axes to the
            // only thing entitled to them.
            //
            // And whose yaw it is depends on whose body it is: while the game is still standing
            // her up out of a save pillar the view rides her own facing instead, so that the
            // get-up turns your head the way her head is turned. See RideHerFacing.
            var gameYaw = gameCameraRotation.eulerAngles.y;
            MeasureCameraYaw(gameYaw);
            rotation = Quaternion.Euler(0f, RideHerFacing(gameYaw), 0f);

            // The bone gives a position; the direction to nudge it in comes from the view.
            //
            // Not from the bone's own axes. `Bip001 Head` is a 3ds Max Biped bone and its local
            // frame has nothing to do with which way the character is looking — measured at 96
            // degrees off the view. Offsetting in that frame sent the eye point sideways and
            // backwards into her body, which looks for all the world like the camera being
            // turned around. Offsetting along the view is indifferent to how the rig was
            // authored.
            var anchor = Steady(head);

            position = anchor
                     + rotation * new Vector3(cfg.HeadOffsetX.Value,
                                              EyeUp + cfg.HeadOffsetY.Value,
                                              EyeForward + cfg.HeadOffsetZ.Value);

            // Taken here because this is where both halves are to hand, and used by anything
            // hung in front of the eyes rather than by the view itself.
            var girl = _playerCamera.wizardGirl;
            if (girl != null) MeasureEyeLevel(girl.transform, position.y);
            else EyeLevel = position.y;

            ReportFacing(gameCameraRotation, head);
            return true;
        }

        /// <summary>
        /// Eye level in world units: the height at which a panel hung in front of you belongs.
        ///
        /// Deliberately not the height of the eye position above. The view rides her head bone,
        /// and the head bone answers to more than gravity — the walk cycle moves it, and the
        /// game's IK turns her head and chest towards the aim target, which this mod puts
        /// wherever you are looking on every frame the wand is not tracked. That closes a loop:
        /// your head moves the aim, the aim moves her head, her head moves the view. A loop with
        /// a frame of delay in it does not settle, it rings, and a panel taking its height from
        /// the far end of it rings along with it.
        ///
        /// Her root does not ring. It is the point the character controller moves, so it changes
        /// when she walks, jumps or takes a stair, and at no other time. What is added to it is
        /// how far her eyes sit above her feet — a fact about the model rather than about the
        /// moment.
        /// </summary>
        public float EyeLevel { get; private set; }

        private float _eyeAboveFeet;
        private bool _eyeSeeded;

        /// <summary>
        /// Keeps the eyes-above-feet figure, filtered hard enough that nothing the head bone
        /// does in the course of a second can reach it.
        ///
        /// Three seconds of time constant. A walk cycle and a nod both pass through the bone at
        /// a hertz or more and come out the other side of this at a fraction of a millimetre,
        /// while a first sample taken while she happened to be crouched, mid-jump or slumped
        /// against a save statue corrects itself long before anyone would go looking. Clamped as
        /// well: a sample read while the rig is half loaded is not a small error, it is a
        /// nonsense, and a filter would carry it for a while rather than reject it.
        /// </summary>
        private void MeasureEyeLevel(Transform body, float eyeHeight)
        {
            var above = Mathf.Clamp(eyeHeight - body.position.y, 0.4f, 2.5f);

            if (!_eyeSeeded)
            {
                _eyeSeeded = true;
                _eyeAboveFeet = above;
            }
            else
            {
                _eyeAboveFeet = Mathf.Lerp(_eyeAboveFeet, above,
                                           1f - Mathf.Exp(-Time.deltaTime / 3f));
            }

            EyeLevel = body.position.y + _eyeAboveFeet;
        }

        /// <summary>
        /// Says once, in numbers, which way everything is pointing.
        ///
        /// "I can see Nobeta's face" has more than one cause — the view could be turned around,
        /// or it could be inside an unhidden head looking at the inside of the face mesh — and
        /// they are not distinguishable from in there. These three readings separate them: if
        /// the camera and the body disagree by about 180 degrees the view is backwards, and if
        /// the head scale is not zero the head was never hidden.
        /// </summary>
        private bool _facingReported;

        private void ReportFacing(Quaternion gameCameraRotation, Transform head)
        {
            if (_facingReported || _playerCamera == null) return;
            var girl = _playerCamera.wizardGirl;
            if (girl == null) return;
            _facingReported = true;

            var camFwd = gameCameraRotation * Vector3.forward;
            var bodyFwd = girl.transform.forward;
            var headFwd = head.forward;

            Plugin.Log.LogInfo($"facing: camera->body {Vector3.Angle(camFwd, bodyFwd):F1} deg, "
                             + $"camera->headBone {Vector3.Angle(camFwd, headFwd):F1} deg, "
                             + $"head localScale {head.localScale}");
        }

        /// <summary>
        /// Points the view where Nobeta is facing, for as long as the game is the one moving
        /// her.
        ///
        /// The game frames a new stage however it likes and does not always park the camera
        /// behind her, so the first thing you see can be her spawn direction from the wrong
        /// side — a camera angle on a monitor, and the way your own body is pointing in a
        /// headset. Loading a save is the hardest case of it: she comes back slumped against a
        /// save pillar and stands up out of it, and the get-up turns her as it goes. There is
        /// therefore no single yaw at the top of the stage that is still the right one by the
        /// time she is on her feet — which is why one shot of her facing, taken on the first
        /// frame that had a head bone, could not work: you woke up looking at the pillar she
        /// had her back to.
        ///
        /// So the view rides her facing for every frame of it, and is handed over when she is:
        /// the moment she is plainly the player's *and* the player has asked for something.
        /// Waking against the pillar then looks like waking against the pillar — she turns, and
        /// the view turns with her, because the view is her head.
        ///
        /// Two things have to hold or the handover is a jolt of its own. The yaw the view leaves
        /// on has to be the yaw the game's camera is holding, and that is `g_fX` — see
        /// <see cref="MeasureCameraYaw"/> for why her yaw is not simply written into it. And
        /// nothing may turn her towards the view while this runs: the view *is* her facing with
        /// the headset's own yaw on top, so turning her to face it would walk her round in a
        /// slow circle. <see cref="BodyFacing"/> stands down on <see cref="ViewIsYours"/> for
        /// exactly that reason.
        /// </summary>
        private float RideHerFacing(float gameYaw)
        {
            if (ViewIsYours) return gameYaw;

            var girl = _playerCamera.wizardGirl;
            if (girl == null) return gameYaw;

            var bodyYaw = girl.transform.eulerAngles.y;

            if (!_riding)
            {
                _riding = true;
                _rodeFrom = bodyYaw;
                _rodeSince = Time.unscaledTime;
                Plugin.Log.LogInfo($"the view rides her facing from {bodyYaw:F1} deg until she "
                                 + $"is yours (state {PlayerStatus.State?.ToString() ?? "<none>"}, "
                                 + $"controllable {PlayerStatus.Controllable}, g_fX "
                                 + $"{_playerCamera.g_fX:F1}, game camera {gameYaw:F1})");
            }

            // Kept under the view as it goes, rather than written once at the end. Movement is
            // camera-relative and the game reads `g_fX` for it, so a `g_fX` that only catches up
            // on the frame the player takes over is a first step in the wrong direction.
            if (_cameraYawKnown) _playerCamera.g_fX = bodyYaw - _cameraYawOffset;

            // Her holding still is *not* a second way out of this, which is worth writing down
            // because it is the obvious one to reach for. She holds perfectly still for as long
            // as she is asleep against the pillar: the game places her in the stage's opening
            // frames and the get-up comes later, so "her yaw has not moved for a second" is true
            // in the middle of exactly the sequence this exists for, and taking it would hand
            // the turning back mid-animation and put her on her feet facing the pillar again.
            // The player asking is the only reading that cannot happen before she is placed.
            var waited = Time.unscaledTime - _rodeSince;
            var asked = PlayerStatus.YoursToDrive && Input.VrControls.PlayerActed;

            if (asked || waited > RidePatience)
            {
                _viewIsYours = true;
                Plugin.Log.LogInfo($"the view is yours after {waited:F1}s "
                                 + $"({(asked ? "you asked for it" : "nothing did, so the wait ran out")}): "
                                 + $"her facing went {_rodeFrom:F1} -> {bodyYaw:F1} deg, "
                                 + $"g_fX left at {_playerCamera.g_fX:F1}");
            }

            return bodyYaw;
        }

        /// <summary>How long the view rides her facing with nothing asking for it, in seconds.</summary>
        private const float RidePatience = 20f;

        private bool _viewIsYours;
        private bool _riding;
        private float _rodeFrom;
        private float _rodeSince;

        /// <summary>
        /// Whether the view's yaw is the player's rather than Nobeta's own facing. False only
        /// while <see cref="RideHerFacing"/> is running, and true throughout when the setting
        /// that arms it is off.
        /// </summary>
        public bool ViewIsYours =>
            _viewIsYours || _playerCamera == null || !Plugin.Instance.AlignViewToBodyOnSpawn.Value;

        /// <summary>
        /// What `g_fX` does to the view, measured on the running game rather than assumed.
        ///
        /// `g_fX` is the value the turn control drives and the only handle there is on the
        /// camera's yaw, but nothing says the number is the direction you are looking in: the
        /// natural quantity for a third-person boom is where the camera sits *around* her, which
        /// points the other way. Turning never had to care, because it adds to `g_fX` and any
        /// fixed offset cancels — and the spawn alignment did care, wrote her yaw into it as
        /// though the two were the same thing, and put the view out by whatever the difference
        /// is. Opening a stage looking at what she has her back to is what that looks like.
        ///
        /// So the difference is read off the game: `g_fX` against the yaw the camera actually
        /// ended up with. Only while `g_fX` has been at rest for long enough for the camera to
        /// have caught up with it, because the camera lags it through the game's own smoothing
        /// and a moving camera disagrees with `g_fX` for a reason that is not an offset — a
        /// stage's opening frames are the case, where the boom is still swinging into place.
        /// Quantised to a half turn, since that is the shape this answer can take and a degree
        /// of residual lag should not become a degree of error — with the raw reading logged
        /// beside it, so a third answer would be visible in the log rather than rounded away.
        ///
        /// Re-read for as long as the game runs rather than settled once. Nothing is riding on
        /// it until the ride writes `g_fX`, which cannot happen before the first reading, and a
        /// later one taken with the camera plainly at rest is worth more than the first: if the
        /// two disagree, the log says so and says which the view was built on.
        /// </summary>
        private void MeasureCameraYaw(float gameYaw)
        {
            var handle = _playerCamera.g_fX;

            // NaN on the first frame of a stage, and NaN compares false: one frame is skipped
            // rather than measured against a value belonging to the camera before this one.
            var atRest = Mathf.Abs(Mathf.DeltaAngle(handle, _lastHandle)) < 0.01f;
            _lastHandle = handle;
            _atRestFor = atRest ? _atRestFor + 1 : 0;
            if (_atRestFor < RestFrames) return;

            var raw = Mathf.DeltaAngle(handle, gameYaw);
            var offset = Mathf.Round(raw / 180f) * 180f;

            if (!_cameraYawKnown)
            {
                Plugin.Log.LogInfo($"g_fX {handle:F1} holds the game's camera at {gameYaw:F1} deg: "
                                 + $"the yaw it means is {raw:F1} deg from the view, taken as "
                                 + $"{offset:F0}");
            }
            else if (Mathf.Abs(Mathf.DeltaAngle(offset, _cameraYawOffset)) > 1f)
            {
                Plugin.Log.LogInfo($"g_fX offset re-measured: {_cameraYawOffset:F0} -> {offset:F0} "
                                 + $"(raw {raw:F1}); the view's yaw was built on the old one.");
            }

            _cameraYawOffset = offset;
            _cameraYawKnown = true;
        }

        /// <summary>
        /// How long `g_fX` must have been still before the camera's yaw is worth reading, in
        /// frames. A tenth of a second at the rates this runs at, which is longer than the
        /// game's own camera smoothing takes to close on a value it is chasing.
        /// </summary>
        private const int RestFrames = 10;

        private float _cameraYawOffset;
        private bool _cameraYawKnown;
        private float _lastHandle = float.NaN;
        private int _atRestFor;

        /// <summary>
        /// Puts the view back on her facing at once, and leaves the yaw the player's.
        ///
        /// Used by recentring, which means the direction as well as the place: doing only the
        /// position leaves you standing where she is and facing somewhere else. It does not
        /// restart the ride above — a recentre is the player asking for something, and handing
        /// the yaw straight back to them is the whole of what they asked for.
        /// </summary>
        public void RealignToBody()
        {
            var girl = _playerCamera != null ? _playerCamera.wizardGirl : null;
            if (girl == null)
            {
                // Nothing to align to yet, so re-arm instead and let the first frame that has a
                // body do it. That is what the ride is for.
                _viewIsYours = false;
                _riding = false;
                return;
            }

            var bodyYaw = girl.transform.eulerAngles.y;
            _playerCamera.g_fX = bodyYaw - (_cameraYawKnown ? _cameraYawOffset : 0f);
            _viewIsYours = true;
            _riding = false;
            Plugin.Log.LogInfo($"recentre: the view goes back to her facing, {bodyYaw:F1} deg");
        }

        private Vector3 _steadyLocal;
        private bool _steadySeeded;

        /// <summary>
        /// The head position, with the walk animation's bob taken out if it is not wanted.
        ///
        /// Smoothed in the character's own space rather than in world space, so walking, turning
        /// and being carried by a platform all pass through untouched while the bone's own
        /// oscillation is flattened. Smoothing the world position instead would fight every
        /// genuine movement she makes.
        /// </summary>
        private Vector3 Steady(Transform head)
        {
            if (Plugin.Instance.HeadBobbing.Value || _playerCamera == null)
            {
                _steadySeeded = false;
                return head.position;
            }

            var girl = _playerCamera.wizardGirl;
            if (girl == null) return head.position;

            var body = girl.transform;
            var local = body.InverseTransformPoint(head.position);

            if (!_steadySeeded)
            {
                _steadySeeded = true;
                _steadyLocal = local;
            }
            else
            {
                var t = 1f - Mathf.Exp(-6f * Time.deltaTime);
                _steadyLocal = Vector3.Lerp(_steadyLocal, local, t);
            }

            return body.TransformPoint(_steadyLocal);
        }

        /// <summary>
        /// Turns off the two things the game does to its camera that stop being charming once
        /// the camera is your head: the breathing sway, and combat shake.
        ///
        /// Both are fine on a monitor and both move the horizon under you in a headset. They
        /// are switched off through the game's own API rather than by fighting the values it
        /// writes, so nothing has to be reapplied per frame.
        /// </summary>
        private void ApplyComfort()
        {
            if (_comfortApplied || _playerCamera == null) return;
            _comfortApplied = true;

            if (Plugin.Instance.DisableRespiration.Value)
            {
                _playerCamera.SetRespiration(false);
                Plugin.Log.LogInfo("camera respiration off");
            }

            if (Plugin.Instance.DisableCameraShake.Value)
            {
                _playerCamera.g_bShakeEnable = false;
                Plugin.Log.LogInfo("camera shake off");
            }
        }

        /// <summary>
        /// Hides the head only while the camera is inside it.
        ///
        /// A switch was the wrong shape for this. The head is a problem exactly when your eyes
        /// are close enough to be within the mesh, and it is wanted the rest of the time —
        /// during a cutscene that pulls back, in the pause camera, or simply when an animation
        /// carries her head away from where the view sits. Measuring the distance answers all of
        /// those with one rule instead of asking the player to predict them.
        ///
        /// Scaling the bone rather than disabling a renderer is deliberate: the mesh is shared
        /// with the rest of the body, so there is no head renderer to switch off, and the hair
        /// is parented to this bone and goes with it.
        /// </summary>
        public void UpdateHeadVisibility(Vector3 cameraPosition)
        {
            if (_head == null) return;

            var distance = Vector3.Distance(cameraPosition, _head.position);
            var hide = distance <= Plugin.Instance.HeadHideDistance.Value;

            if (hide == _hidden) return;
            _hidden = hide;

            if (hide)
            {
                if (!_headScaleSaved)
                {
                    _headScale = _head.localScale;
                    _headScaleSaved = true;
                }
                _head.localScale = Vector3.zero;
            }
            else if (_headScaleSaved)
            {
                _head.localScale = _headScale;
            }
        }

        private bool _hidden;

        public void RestoreHead()
        {
            if (_headScaleSaved && _head != null) _head.localScale = _headScale;
            _headScaleSaved = false;
            _hidden = false;
        }
    }
}
