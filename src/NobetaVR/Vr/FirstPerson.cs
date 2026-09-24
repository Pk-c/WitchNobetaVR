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

        /// <summary>
        /// The head bone's own scale, whether or not it is hidden right now. Exposed for
        /// <see cref="ShadowBody"/>, which draws her shadow from a head that is never hidden.
        /// </summary>
        public Vector3 HeadRestScale =>
            _headScaleSaved ? _headScale : _head != null ? _head.localScale : Vector3.one;

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

            // A new stage is a new spawn, so the view goes back to riding her facing until she
            // is the player's again — and what the player did in the stage before is not an
            // answer to whether they have asked for anything in this one.
            _viewIsYours = false;
            _riding = false;
            _lastHandle = float.NaN;
            _lastGameYaw = float.NaN;
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
        /// for finding the character root.
        ///
        /// Re-resolved while null, since the skin loads asynchronously: on the opening frames of
        /// a stage the chain exists but ends in nothing.
        /// </summary>
        private Transform Head()
        {
            if (_head != null) return _head;

            // Whatever was known about the last head goes with it. `girl.transform` outlives a
            // skin change -- a costume, or the story skin a cutscene puts on -- so the root is
            // the same object while every bone under it has been destroyed and rebuilt, and a
            // null `_head` is the one place that notices. Left standing, `_hidden` says "the
            // head is scaled away" about a bone that no longer exists: `UpdateHeadVisibility`
            // then finds the answer it wants already recorded and returns without touching the
            // new one, so her head sits in the middle of the view for as long as the costume
            // is on. Dropped rather than restored, since there is nothing left to restore to.
            ForgetHead();

            if (_playerCamera == null) return null;

            var girl = _playerCamera.wizardGirl;
            var skin = girl != null ? girl.skinController : null;
            var ik = skin != null ? skin.ik : null;
            var helper = ik != null ? ik.head : null;
            if (helper == null) return null;

            var root = girl != null ? girl.transform : helper.root;
            _head = FindHeadBone(root, helper);

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

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var name = t.name;
                if (name.IndexOf("head", System.StringComparison.OrdinalIgnoreCase) < 0) continue;

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

            var chosen = exact ?? partial;
            if (chosen == null)
            {
                Plugin.Log.LogWarning($"No head bone found; falling back to the IK helper "
                                    + $"'{fallback.name}', which will put the view inside her head.");
                return fallback;
            }
            return chosen;
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
                _rodeSince = Time.unscaledTime;
            }

            // Kept under the view as it goes, rather than written once at the end. Movement is
            // camera-relative and the game reads `g_fX` for it, so a `g_fX` that only catches up
            // on the frame the player takes over is a first step in the wrong direction.
            //
            // Written whether or not the offset has been measured yet, with the same fallback
            // <see cref="RealignToBody"/> has always used. Waiting for a measurement was the
            // worse of the two failures: the handle then keeps whatever the stage load left in
            // it for the whole ride, and the view is handed over to a camera that was never
            // asked to point anywhere — which at a save statue is the camera the wake framed her
            // face with, half a turn from the way she is standing up.
            _playerCamera.g_fX = bodyYaw - (_cameraYawKnown ? _cameraYawOffset : 0f);

            // Her holding still is *not* a second way out of this, which is worth writing down
            // because it is the obvious one to reach for. She holds perfectly still for as long
            // as she is asleep against the pillar: the game places her in the stage's opening
            // frames and the get-up comes later, so "her yaw has not moved for a second" is true
            // in the middle of exactly the sequence this exists for, and taking it would hand
            // the turning back mid-animation and put her on her feet facing the pillar again.
            // The player asking is the only reading that cannot happen before she is placed.
            var waited = Time.unscaledTime - _rodeSince;
            var asked = PlayerStatus.YoursToDrive && Input.VrControls.PlayerActed;

            if (asked || waited > RidePatience) _viewIsYours = true;

            return bodyYaw;
        }

        /// <summary>How long the view rides her facing with nothing asking for it, in seconds.</summary>
        private const float RidePatience = 20f;

        private bool _viewIsYours;
        private bool _riding;
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
        /// ended up with. Only while the player is driving both and both have come to rest,
        /// which is the whole of what makes the two comparable — see the gate below for the
        /// spawn that taught it that. Quantised to a half turn, since that is the shape this
        /// answer can take and a degree of residual lag should not become a degree of error.
        ///
        /// Re-read for as long as the game runs rather than settled once, and zero until the
        /// first reading lands. Zero is what <see cref="RealignToBody"/> has always assumed and
        /// what the game has measured as every time it has been asked; a later reading taken
        /// with both ends plainly at rest is worth more than an early one.
        /// </summary>
        private void MeasureCameraYaw(float gameYaw)
        {
            // Only while the player is driving both, which `g_fX` holding still does not show
            // and at a save statue actively hides. Nothing moves the handle while she sits
            // against the pillar — the player has no controls yet — so the stillest reading in
            // the game is taken from the one camera in it that owes the handle nothing: the
            // wake's own framing, pointed at her. Quantised to the nearest half turn, a camera
            // looking at her face rather than along her back reads as an offset of 180, the ride
            // writes that half turn into `g_fX`, and she stands up with the camera behind her
            // face instead of her back. Which spawn gets it depends on where the stage's camera
            // happened to be sitting when the tenth frame came round, and that is the "sometimes"
            // in the report.
            //
            // In `Normal`, with her the player's to move and the view already handed over, the
            // camera's yaw is the handle plus the constant this is after and nothing else.
            if (!ViewIsYours || !PlayerStatus.YoursToDrive)
            {
                _atRestFor = 0;
                return;
            }

            var handle = _playerCamera.g_fX;

            // Both ends still, not just the handle. The camera lags it through the game's own
            // smoothing, so a handle at rest for a tenth of a second is not yet a camera that
            // has arrived — and the difference between the two is read here as an offset.
            //
            // NaN on the first frame of a stage, and NaN compares false: one frame is skipped
            // rather than measured against a value belonging to the camera before this one.
            var atRest = Mathf.Abs(Mathf.DeltaAngle(handle, _lastHandle)) < 0.01f
                      && Mathf.Abs(Mathf.DeltaAngle(gameYaw, _lastGameYaw)) < 0.1f;
            _lastHandle = handle;
            _lastGameYaw = gameYaw;
            _atRestFor = atRest ? _atRestFor + 1 : 0;
            if (_atRestFor < RestFrames) return;

            var raw = Mathf.DeltaAngle(handle, gameYaw);
            var offset = Mathf.Round(raw / 180f) * 180f;

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
        private float _lastGameYaw = float.NaN;
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
        }

        /// <summary>
        /// Points the game's camera so that Nobeta's forward is straight ahead of the player's
        /// nose, and hands the view's yaw over without a ride. Returns the yaw the camera is
        /// being sent to, or false when there is no body to read.
        /// </summary>
        /// <param name="headsetYaw">Where the player is physically facing, flattened.</param>
        /// <param name="cameraYaw">The yaw the game's camera will settle on.</param>
        /// <remarks>
        /// What the view shows in a stage is the game camera's yaw with the headset's own laid
        /// on top, and that second term is a player's accumulated physical turning: half a turn
        /// of it is an ordinary way to be standing after a fight. So aiming the camera down her
        /// forward — which is all <see cref="RealignToBody"/> and the ride can do — aims the
        /// player's nose down her forward *plus* however they happen to be standing, and the
        /// answer is right by exactly as much as they are square to the room.
        ///
        /// <para>
        /// Taking the headset off the camera's yaw is what closes that gap: the two terms then
        /// add up to her facing and nothing else, for a player standing any way at all. It is
        /// the direction half of a recentre, and it is spent where a recentre cannot be asked
        /// for — the frame a spawn hands the controls over, where being turned the wrong way is
        /// the first thing that happens to you.
        /// </para>
        ///
        /// <para>
        /// Called every frame of the stand-back rather than once at the handover. The game's
        /// camera eases towards <c>g_fX</c> rather than jumping to it, so a value written on
        /// the last frame arrives some way after the view has already taken its direction from
        /// it. Written throughout, the camera has long since settled, and the handover has
        /// nothing left to converge.
        /// </para>
        /// </remarks>
        public bool AimAlongBody(float headsetYaw, out float cameraYaw)
        {
            cameraYaw = 0f;

            var girl = _playerCamera != null ? _playerCamera.wizardGirl : null;
            if (girl == null) return false;

            cameraYaw = girl.transform.eulerAngles.y - headsetYaw;
            _playerCamera.g_fX = cameraYaw - (_cameraYawKnown ? _cameraYawOffset : 0f);

            // The ride is the other way of carrying a view through a get-up, and it is the one
            // that leaves the headset's yaw on top. Standing it down here is not an
            // optimisation: left armed, it would take the view back off this the moment first
            // person resumed.
            _viewIsYours = true;
            _riding = false;
            return true;
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

            if (Plugin.Instance.DisableRespiration.Value) _playerCamera.SetRespiration(false);
            if (Plugin.Instance.DisableCameraShake.Value) _playerCamera.g_bShakeEnable = false;
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
        ///
        /// A cutscene is the exception to the distance rule: the game's camera is framing her,
        /// and a push-in on her face can bring it inside the hide distance, which took her head
        /// off in the middle of the shot. While the game frames the view her head always stays.
        /// </summary>
        /// <param name="cameraPosition">Where the view is this frame.</param>
        /// <param name="gameIsFraming">Whether a cutscene is placing the camera.</param>
        public void UpdateHeadVisibility(Vector3 cameraPosition, bool gameIsFraming)
        {
            if (_head == null) return;

            var distance = Vector3.Distance(cameraPosition, _head.position);
            var hide = !gameIsFraming && distance <= Plugin.Instance.HeadHideDistance.Value;

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
            ForgetHead();
        }

        /// <summary>
        /// Forgets that the head was ever hidden, without writing to the bone. For the case
        /// <see cref="Head"/> describes, where there is no bone left to write to.
        /// </summary>
        private void ForgetHead()
        {
            _headScaleSaved = false;
            _headScale = Vector3.one;
            _hidden = false;
        }
    }
}
