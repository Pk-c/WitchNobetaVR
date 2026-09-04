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
            _candidatesReported = false;
            _aligned = false;
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
            AlignToBody();

            var cfg = Plugin.Instance;

            // Yaw from the game, pitch and roll from your neck.
            //
            // Taking the game camera's full rotation would add its pitch to the headset's, so
            // looking up would pitch twice and the horizon would tilt with every camera shake.
            // Yaw alone keeps stick turning working while leaving the other two axes to the
            // only thing entitled to them.
            rotation = Quaternion.Euler(0f, gameCameraRotation.eulerAngles.y, 0f);

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

            ReportFacing(gameCameraRotation, head);
            return true;
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
        /// Points the view where Nobeta is facing, once, when a stage opens.
        ///
        /// The game is free to frame a new stage however it likes, and it does not always park
        /// the camera behind her — so the first thing you see can be her spawn direction from
        /// the wrong side, which in a headset means starting the level facing backwards. On a
        /// monitor that is a camera angle; in VR it is where your body is pointing.
        ///
        /// Now that the body follows the view, this cannot be left to correct itself: she would
        /// simply turn to match the wrong direction and stay there. So the camera's own yaw is
        /// set to hers, through the same `g_fX` the turn control writes, which keeps the view,
        /// the movement frame and the game's idea of the camera as one value.
        /// </summary>
        private void AlignToBody()
        {
            if (_aligned || !Plugin.Instance.AlignViewToBodyOnSpawn.Value) return;
            if (_playerCamera == null) return;

            var girl = _playerCamera.wizardGirl;
            if (girl == null) return;

            _aligned = true;

            var bodyYaw = girl.transform.eulerAngles.y;
            Plugin.Log.LogInfo($"aligning view to body at spawn: g_fX {_playerCamera.g_fX:F1} -> {bodyYaw:F1}");
            _playerCamera.g_fX = bodyYaw;
        }

        private bool _aligned;

        /// <summary>
        /// Re-arms the alignment so the next frame points the view at her again. Used by
        /// recentring: "put my head back" means the direction as well as the place, and doing
        /// only the position leaves you standing correctly but facing the wrong way.
        /// </summary>
        public void RealignToBody() => _aligned = false;

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
