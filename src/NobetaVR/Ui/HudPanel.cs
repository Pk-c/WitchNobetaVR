using System;
using Il2CppInterop.Runtime;
using NobetaVR.Vr;
using UnityEngine;
using UnityEngine.Video;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Puts the game's interface on a panel in front of you.
    ///
    /// The interface is captured, not rebuilt. Every screen-space canvas is redirected through
    /// a camera of our own that draws into a render texture, and that texture is shown on a
    /// quad floating in the world. Unity keeps full control of the layout, so TextMeshPro, the
    /// game's own animations, and its habit of hiding elements by sliding them off screen all
    /// keep working exactly as they did. Rebuilding the HUD would mean re-implementing all of
    /// that and re-breaking it on every game update.
    ///
    /// Screen-space overlay canvases are drawn straight to the display after everything else,
    /// which is why none of the interface was visible in the headset: there was nothing wrong
    /// with it, it simply was not part of the scene either eye renders.
    /// </summary>
    public sealed class HudPanel : MonoBehaviour
    {
        public HudPanel(IntPtr ptr) : base(ptr) { }

        private Camera _capture;
        private RenderTexture _texture;
        private Transform _panel;
        private Material _material;

        private float _nextScan;
        private int _canvasCount = -1;

        /// <summary>Layers the captured canvases were last seen on; reported, not enforced.</summary>
        private int _layers;
        private bool _failed;
        private Vector3 _direction;

        /// <summary>The active scene as last seen, which is how a scene change is noticed.</summary>
        private string _scene;

        /// <summary>Until when the scan runs at <see cref="Eager"/> rather than once a second.</summary>
        private float _eagerUntil;

        /// <summary>Whether the panel has stepped aside for a load; see <see cref="Settle"/>.</summary>
        private bool _hidden;

        /// <summary>The scene it stepped aside in, which is the one it is waiting to leave.</summary>
        private string _hiddenFrom;

        /// <summary>Whether a load has been seen from its start, so its end is acted on once.</summary>
        private bool _armed;

        private float _giveUp;


        /// <summary>Depth-test state as last written to the material; see <see cref="DepthTest"/>.</summary>
        private bool? _onTop;

        internal static HudPanel Instance { get; private set; }

        private void Awake() => Instance = this;

        private void LateUpdate()
        {
            if (_failed) return;

            if (!Plugin.Instance.HudEnabled.Value)
            {
                // The setting says the interface is not in the headset, and it has to stop the
                // capture as well as the panel. Returning here without doing either was the
                // worst of both: the panel went on drawing whatever it last had, and a 1080p
                // camera went on filling it, for a session in which the player had turned the
                // whole thing off.
                Show(false);
                Capture(false);
                return;
            }

            if (_texture == null && !Build()) return;

            Rescan();
            Settle();
            DepthTest();

            // Shown here, placed later in the frame from the view; see FollowView. Both land
            // before anything renders, and it is placed even while hidden, so that it comes
            // back where your head is rather than snapping in from wherever the transition
            // left it.
            Show(!_hidden);
            Capture(!_hidden);
        }

        /// <summary>Whether the capture camera is currently allowed to render.</summary>
        private bool? _capturing;

        /// <summary>
        /// Runs the capture camera only while something is looking at what it draws.
        ///
        /// It is a full camera over a 1920x1080 target that culls every layer, and it was
        /// enabled for the life of the process — through every load, every fade, and every
        /// stretch with the panel stood aside. That is a render pass and a scene cull per frame
        /// for a texture hanging on a quad nobody can see.
        ///
        /// Safe to switch precisely because the panel is switched with it: a disabled camera
        /// leaves the last image in the texture, and the one frame where that would show is the
        /// frame it comes back on — which <see cref="Build"/> settles by giving this camera a
        /// depth well below the game's, so it has already redrawn by the time anything renders
        /// the panel it feeds.
        /// </summary>
        private void Capture(bool on)
        {
            if (_capture == null || _capturing == on) return;

            _capturing = on;
            _capture.enabled = on;
        }

        /// <summary>
        /// Places the panel, driven from the view at the moment the view is final.
        ///
        /// Not from <c>LateUpdate</c>, where this used to be called. The head pose is written
        /// inside the game's own LateUpdate, after every one of the mod's, so the eye position
        /// read there belonged to the previous frame -- and this panel is pinned rigidly to
        /// that position by design, which turns one frame of staleness into a shake rather
        /// than into lag.
        /// </summary>
        internal static void FollowView()
        {
            var self = Instance;
            if (self == null || self._failed || self._texture == null) return;
            if (!Plugin.Instance.HudEnabled.Value) return;

            self.Follow();
        }

        /// <summary>
        /// Puts the panel in front of the world rather than in it.
        ///
        /// A quad hung at a fixed distance is geometry like any other, so a wall, a crate or an
        /// enemy closer than that distance occludes it — and the interface disappearing behind
        /// scenery is a bug from the player's side however correct it is from the renderer's.
        /// Turning the depth test off leaves the panel drawn over everything, which is what a
        /// screen-space overlay did on the flat game and what the interface is expected to do.
        ///
        /// Written as a material property because that is how uGUI's own shaders take it:
        /// <c>UI/Default</c> declares <c>ZTest [unity_GUIZTestMode]</c> precisely so a canvas
        /// can set it. <c>_ZTest</c> is set alongside it for the other candidates in
        /// <see cref="TransparentShader"/>, and a property a shader does not declare is
        /// ignored rather than an error.
        ///
        /// Tracked rather than set every frame: this is a live setting, and re-uploading it on
        /// frames where nothing changed would be a material touch per eye for nothing.
        /// </summary>
        private void DepthTest()
        {
            var wanted = Plugin.Instance.HudDrawOnTop.Value;
            if (_material == null || _onTop == wanted) return;

            _onTop = wanted;

            var mode = (int)(wanted
                ? UnityEngine.Rendering.CompareFunction.Always
                : UnityEngine.Rendering.CompareFunction.LessEqual);

            _material.SetInt("unity_GUIZTestMode", mode);
            _material.SetInt("_ZTest", mode);
        }

        // -- construction --------------------------------------------------------------

        private bool Build()
        {
            var cfg = Plugin.Instance;
            var shader = TransparentShader.Find();
            if (shader == null)
            {
                // Nothing to fall back on: an opaque shader here hangs a solid white slab
                // in front of the player's face, which is worse than no interface at all.
                Plugin.Log.LogError("The HUD panel would be an opaque slab. Leaving it off.");
                _failed = true;
                return false;
            }

            // 1080p. The capture camera draws the interface and nothing else, so this costs
            // almost nothing to render and there is no other size anyone would want.
            _texture = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32) { name = "NobetaVR HUD" };
            _texture.Create();
            ClearToTransparent(_texture);

            _capture = new GameObject("NobetaVR HUD Camera").AddComponent<Camera>();
            UnityEngine.Object.DontDestroyOnLoad(_capture.gameObject);
            _capture.gameObject.hideFlags = HideFlags.HideAndDontSave;
            _capture.clearFlags = CameraClearFlags.SolidColor;

            // Transparent black, not black. Clearing to opaque black is the other half of the
            // white-slab mistake: the panel then shows a black rectangle over the whole view.
            _capture.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _capture.targetTexture = _texture;
            _capture.stereoTargetEye = StereoTargetEyeMask.None;   // never render this one in stereo
            _capture.orthographic = false;
            _capture.allowHDR = false;
            _capture.allowMSAA = false;

            // Before the game's own cameras, which is what makes the texture this frame's
            // rather than the last one's. Unity orders cameras by depth and gives no order at
            // all to two that share one, so the default left this racing the view it feeds --
            // and it is what lets the capture be switched off with the panel at all: whenever
            // it comes back, it has redrawn before anything renders the quad.
            _capture.depth = -100f;
            // Every layer. Which sounds reckless and is the opposite: what keeps the world out
            // of this capture is where the camera stands and how little depth in front of it it
            // can see, not the mask — see Park. Culling by the layers of the canvases we found
            // was belt on top of that, and the belt was cutting: the mask is built from *root*
            // canvases, Unity culls a nested canvas by its own layer, and the two together mean
            // any sub-canvas on a layer no root canvas happened to use is interface that
            // silently does not exist. The title screen showed it — one root canvas on layer 5,
            // mask 0x20, and the options page nowhere to be seen.
            _capture.cullingMask = ~0;

            Park(_capture.transform);

            _material = new Material(shader) { mainTexture = _texture };

            // Drawn after the cutscene frame, which is an overlay of its own at 3900. The bars
            // are there to frame what the game is showing you, and eating the subtitles while
            // doing it would be the one way they could make a cutscene worse.
            _material.renderQueue = 3950;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "NobetaVR HUD Panel";
            UnityEngine.Object.DontDestroyOnLoad(quad);
            quad.hideFlags = HideFlags.HideAndDontSave;

            // The collider would put an invisible wall in front of the player.
            var collider = quad.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.material = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _panel = quad.transform;

            // Layer 0, like every other object this mod hangs in the world, so that whatever
            // the game's own camera renders, it renders this too. What keeps it out of the
            // capture is Park, not the layer.
            _panel.gameObject.layer = 0;

            Plugin.Log.LogInfo($"HUD panel built: {_texture.width}x{_texture.height}, "
                             + $"shader '{shader.name}', capture parked at {_capture.transform.position}");
            return true;
        }

        /// <summary>How far from the canvas plane the capture camera can see, either way.</summary>
        private const float Slab = 0.5f;

        /// <summary>
        /// Stands the capture camera somewhere nothing else is, and stops it seeing further
        /// than the interface.
        ///
        /// The camera has to be allowed to render the Default layer, because the game leaves
        /// canvases on it and a canvas outside the mask is interface that silently stops
        /// existing. But Default is also the world's layer and this mod's own -- the panel, the
        /// reticle, the wrist gauges, the fade -- so on the mask alone the capture would draw
        /// the level and, worse, the panel itself, feeding the texture back into its own
        /// picture.
        ///
        /// <para>
        /// The way out is that this camera does not have to be anywhere in particular. A
        /// <c>ScreenSpaceCamera</c> canvas is placed by Unity in front of its camera every
        /// frame, at <c>planeDistance</c>, so the canvases follow wherever this is put and the
        /// captured image is identical. Parked well below the world and able to see half a
        /// metre either side of that one plane, it can only ever render what is placed against
        /// it -- which is exactly and only the interface. No layer has to be guessed at, and
        /// nothing of the game's is modified to make it true.
        /// </para>
        ///
        /// <para>
        /// A thousand units, not a million: far enough that no level reaches it, near enough
        /// that single-precision still resolves this canvas to a fraction of a pixel. The depth
        /// the whole thing depends on is asserted every scan; see <see cref="Rescan"/>.
        /// </para>
        /// </summary>
        private static void Park(Transform camera)
        {
            camera.position = new Vector3(0f, -1000f, 0f);
            camera.rotation = Quaternion.identity;

            var component = camera.GetComponent<Camera>();
            component.nearClipPlane = CanvasPlane - Slab;
            component.farClipPlane = CanvasPlane + Slab;
        }

        /// <summary>Where a captured canvas first lands, in metres in front of the camera.</summary>
        private const float CanvasPlane = 1f;

        /// <summary>The captured canvases, ordered as the game wants them drawn.</summary>
        private readonly System.Collections.Generic.List<Canvas> _stack = new();

        /// <summary>
        /// Pins the captured canvases to the canvas plane, and reports the order they are in.
        ///
        /// The report is the useful half. Which piece of interface is in front of which is the
        /// question this whole arrangement has to get right, and for a long time the log could
        /// not answer it at all -- the count of canvases said nothing about their order or
        /// their names. It is logged only when it changes, so a steady session says it once.
        /// </summary>
        private void Stack()
        {
            if (_stack.Count == 0) return;

            // Insertion sort, because it is stable: canvases sharing a sortingOrder keep the
            // order they were found in rather than trading places from one pass to the next,
            // and an interface that reshuffles itself once a second would be worse than one
            // that is merely in the wrong order.
            for (var i = 1; i < _stack.Count; i++)
            {
                var canvas = _stack[i];
                var order = canvas.sortingOrder;
                var j = i - 1;

                while (j >= 0 && _stack[j].sortingOrder > order)
                {
                    _stack[j + 1] = _stack[j];
                    j--;
                }

                _stack[j + 1] = canvas;
            }

            // All on the one plane. Spreading them through the capture's depth by sortingOrder
            // was tried, on the theory that coplanar canvases leave their order to the
            // renderer, and it changed nothing -- because the game already gives these distinct
            // sortingOrders and Unity already honours them between canvases at equal distance.
            // The order was never the fault, so the depth stays simple and this reports rather
            // than rearranges.
            var report = new System.Text.StringBuilder();

            for (var i = 0; i < _stack.Count; i++)
            {
                _stack[i].planeDistance = CanvasPlane;

                if (i > 0) report.Append(" < ");
                report.Append(_stack[i].name).Append('(').Append(_stack[i].sortingOrder).Append(')');
            }

            // Only when it changes. Which interface is in front of which is the question this
            // whole arrangement exists to get right, and a session that never reshuffles should
            // say so once rather than once a second.
            var line = report.ToString();
            if (line == _order) return;

            _order = line;
            Plugin.Log.LogInfo($"HUD panel, back to front: {line}");
        }

        /// <summary>The order as last reported, so a steady stack is not logged over and over.</summary>
        private string _order;

        /// <summary>
        /// A new render texture holds whatever was in that memory. Clearing it to transparent
        /// before anything can draw it is what stops the panel appearing as a bright rectangle
        /// on the first frame, which is the classic way this goes wrong.
        /// </summary>
        private static void ClearToTransparent(RenderTexture rt)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            RenderTexture.active = previous;
        }

        // -- capture -------------------------------------------------------------------

        /// <summary>How often the scan runs just after a scene change, and for how long.</summary>
        private const float Eager = 0.1f;
        private const float EagerFor = 5f;

        /// <summary>
        /// Redirects the game's screen-space canvases through the capture camera.
        ///
        /// Rescanned on a timer rather than done once: canvases are created and destroyed per
        /// stage and per menu, and one that appears later would otherwise go on drawing itself
        /// straight to a display nobody is looking at.
        /// </summary>
        private void Rescan()
        {
            // A scene change is the one moment the interface is guaranteed to be different
            // objects, and the once-a-second timer can be a full second late to it. That second
            // is a second in which the new scene's canvases are still drawing themselves to a
            // display nobody is looking at and the culling mask still describes the last
            // scene's -- which is exactly the window the panel is wrong in. The scene name
            // forces the pass, and the scan stays quick for a few seconds afterwards, because
            // the canvases do not all exist on the frame the scene becomes active.
            var active = ActiveScene.Name;
            if (active != _scene)
            {
                _scene = active;
                _nextScan = 0f;
                _eagerUntil = Time.unscaledTime + EagerFor;
            }

            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + (Time.unscaledTime < _eagerUntil ? Eager : 1f);

            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>());
            if (found == null) return;

            var mask = 0;
            var redirected = 0;

            _stack.Clear();

            for (var i = 0; i < found.Length; i++)
            {
                var canvas = found[i].TryCast<Canvas>();
                if (canvas == null || canvas.rootCanvas != canvas) continue;

                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = _capture;
                    canvas.planeDistance = CanvasPlane;
                    redirected++;
                }

                if (canvas.worldCamera != _capture) continue;

                _stack.Add(canvas);
                mask |= 1 << canvas.gameObject.layer;
            }

            // Every pass, not only on the frame a canvas was taken over: the set changes as
            // menus and stages come and go, and the depth each one sits at is meaningful only
            // relative to the others.
            Stack();

            RedirectVideos();

            // Reported rather than enforced. The camera sees every layer -- see Build for why
            // that is safe and why culling by these was not -- but which layers the interface
            // is spread across is still the first thing worth knowing when a piece of it goes
            // missing, so the reading is kept and the decision is not.
            if (mask != 0 && mask != _layers)
            {
                _layers = mask;
                Plugin.Log.LogInfo($"HUD canvases on layers 0x{mask:X}");
            }

            if (redirected > 0 || _canvasCount != found.Length)
            {
                // A canvas appearing or disappearing means the interface is being rebuilt --
                // a page opening, a screen being pushed -- and its parts do not all arrive on
                // the frame the first of them does. Same reason a scene change goes eager, and
                // the difference between a menu that appears and a menu that appears a second
                // later missing half of itself.
                if (_canvasCount >= 0 && _canvasCount != found.Length)
                    _eagerUntil = Time.unscaledTime + 1f;

                _canvasCount = found.Length;
                if (redirected > 0) Plugin.Log.LogInfo($"redirected {redirected} canvas(es) to the HUD panel");
            }
        }

        /// <summary>
        /// Moves a video the game draws straight onto a camera's image onto the panel instead.
        ///
        /// The end credits are the case: <c>StaffManager</c> plays <c>Staff.mp4</c> in
        /// <c>CameraNearPlane</c> mode, aspect <c>FitOutside</c>, on the scene's only camera. That
        /// is a full-screen video on a monitor. In the headset it is a picture welded to the
        /// face that fills each eye's whole field of view, and cropped to the eye's nearly
        /// square aspect on top of it, so what was left to read was the middle of the scroll,
        /// blown up past the edges of vision.
        ///
        /// <para>
        /// Pointed at the capture camera, the same video lands in the panel's 1080p texture:
        /// 16:9, at the panel's distance and size, and as live as any other setting that moves
        /// the panel. The far plane rather than the near one, because the canvases the scene
        /// draws over the video — its fade to black and the skip prompt — are in this capture
        /// too, and a video on the near plane would be drawn over them. <c>FitInside</c>, so a
        /// video that is not 16:9 is letterboxed rather than cropped.
        /// </para>
        ///
        /// <para>
        /// Rechecked every scan rather than done once, like the canvases: the game may assign
        /// the camera again when it prepares or plays the video, and a player that reverted
        /// would go straight back to the face.
        /// </para>
        /// </summary>
        private void RedirectVideos()
        {
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<VideoPlayer>());
            if (found == null) return;

            for (var i = 0; i < found.Length; i++)
            {
                var player = found[i].TryCast<VideoPlayer>();
                if (player == null) continue;

                var mode = player.renderMode;
                if (mode != VideoRenderMode.CameraNearPlane && mode != VideoRenderMode.CameraFarPlane) continue;

                var target = player.targetCamera;
                if (target == _capture
                    && mode == VideoRenderMode.CameraFarPlane
                    && player.aspectRatio == VideoAspectRatio.FitInside) continue;

                player.renderMode = VideoRenderMode.CameraFarPlane;
                player.aspectRatio = VideoAspectRatio.FitInside;
                player.targetCamera = _capture;

                Plugin.Log.LogInfo($"video '{player.name}' moved from {mode} on "
                                 + $"'{(target != null ? target.name : "<none>")}' to the HUD panel");
            }
        }

        // -- transitions ---------------------------------------------------------------

        /// <summary>The fade level at or under which a transition counts as over.</summary>
        private const float Down = 0.02f;

        /// <summary>
        /// How far into a load the panel steps aside, and how far back down counts as a new
        /// load beginning.
        ///
        /// Late on purpose. The loading screen is the one piece of interface that is genuinely
        /// worth seeing during a load -- it is the only thing telling you the game has not
        /// hung -- so the panel carries it for as long as it can and leaves only for the last
        /// tenth, which is where the stage starts arriving and the interface behind the black
        /// starts being built.
        /// </summary>
        private const float HideAt = 0.9f;
        private const float Rearm = 0.5f;

        /// <summary>The longest the panel will ever wait before it is owed to the player again.</summary>
        private const float GiveUp = 20f;

        /// <summary>
        /// Steps the panel aside for the end of a load, and brings it back when the new scene
        /// has arrived and the fade over it is done.
        ///
        /// The panel is drawn over the fade deliberately -- a transition is something you read
        /// subtitles and prompts through, and an interface that vanished behind every fade
        /// would be worse than one that occasionally shows too much. The end of a load is the
        /// exception that proves it. There the fade is not framing the interface, it is hiding
        /// a stage that has not finished building: menus, message boxes and a results screen
        /// all live for a moment in whatever state their prefabs were saved in, and the panel
        /// hands the player every one of them at once, laid over the loading screen, in a room
        /// the game has taken the trouble to black out.
        ///
        /// <para>
        /// The cue is the loading progress rather than the scene change, and that is the whole
        /// difference between this and hiding too much. A scene change is late -- the interface
        /// is already being built by then -- but a scene change is also what the *previous*
        /// load ends with, so triggering on it took the loading screen away for the whole of
        /// every load. Progress says exactly what was wanted: carry the loading screen almost
        /// to the end, and leave for the last tenth.
        /// </para>
        ///
        /// <para>
        /// Coming back needs both halves. The fade alone is not enough, because it dips between
        /// the loading screen going and the stage's own fade-in arriving, and a panel that
        /// returned in that dip would return for precisely the frames this exists to cover. So
        /// the scene must have changed as well: the thing being waited for is a stage that is
        /// up and settled, and nothing less says that.
        /// </para>
        ///
        /// <para>
        /// Bounded at <see cref="GiveUp"/>, because the alternative to a bound here is an
        /// interface that never comes back. Whatever went wrong, the panel is owed to the
        /// player eventually.
        /// </para>
        /// </summary>
        private void Settle()
        {
            var progress = LoadingProgress.Value;

            // Armed by seeing a load from near its beginning, so that a value left sitting at
            // one when the last load finished cannot hide the panel again the moment it returns.
            if (progress < Rearm) _armed = true;

            if (!_hidden && _armed && progress >= HideAt)
            {
                _armed = false;
                _hidden = true;
                _hiddenFrom = _scene;
                _giveUp = Time.unscaledTime + GiveUp;

                Plugin.Log.LogInfo($"HUD panel stands aside at {progress:P0} of the load");
            }

            if (!_hidden) return;

            // With the VR fade off the game paints its own black straight onto the panel, which
            // covers the same ground. There is nothing to wait for, and waiting would only
            // blank the interface for no gain.
            if (!Plugin.Instance.VrFade.Value) { Return("the VR fade is off"); return; }

            if (Time.unscaledTime > _giveUp) { Return("it waited long enough"); return; }

            if (_scene != _hiddenFrom && Vr.ViewFade.Amount < Down) Return($"'{_scene}' is up");
        }

        private void Return(string why)
        {
            _hidden = false;
            Plugin.Log.LogInfo($"HUD panel back: {why}");
        }

        private void Show(bool shown)
        {
            if (_panel == null) return;
            if (_panel.gameObject.activeSelf != shown) _panel.gameObject.SetActive(shown);
        }

        // -- placement -----------------------------------------------------------------

        /// <summary>
        /// Keeps the panel in front of you: rigid when you move, damped when you turn.
        ///
        /// The two want opposite treatments and one lerp cannot give both. Damping the panel's
        /// world position makes it lag when you walk, so it drifts towards you when you stop
        /// and away when you set off — which reads as the interface sliding about the room.
        /// Damping only the *direction* it sits in, and pinning it rigidly to your head for
        /// position, gives what is actually wanted: walk and it comes with you exactly, turn
        /// your head and it swings round to catch up a moment later.
        /// </summary>
        /// <summary>The frame the follow filter last advanced on; see <see cref="Follow"/>.</summary>
        private int _followedFrame = -1;

        private void Follow()
        {
            var cfg = Plugin.Instance;
            var camera = VrCamera.CameraTransform;
            if (camera == null || _panel == null) return;

            // Not the flattened forward vector, which is mostly noise once the gaze is well
            // off level and made the panel shiver whenever the head looked down. See ViewAnchor.
            var look = ViewAnchor.YawForward(
                camera, _direction.sqrMagnitude > 0.0001f ? _direction : Vector3.forward);

            if (_direction.sqrMagnitude < 0.0001f) _direction = look;

            var speed = cfg.HudFollowSpeed.Value;

            if (speed <= 0f)
            {
                // Rigid. The panel goes exactly where you are looking, with no catching up
                // left to see -- which is the setting to reach for if the interface appears to
                // chase the head, and the control experiment for the same complaint: a rigid
                // panel that still moves against the world is not the following doing it.
                _direction = look;
            }
            else if (_followedFrame != Time.frameCount)
            {
                // Once a frame, however many times this is called in one.
                //
                // It is called twice now: once as the view is applied and once again from the
                // render-time latch, which is a placement rather than a second frame's worth
                // of catching up. Stepping the filter both times would quietly double
                // HudFollowSpeed, so the step is taken on the first call and the second only
                // re-places the panel against the fresher eye -- which is the whole point of
                // being called again.
                _followedFrame = Time.frameCount;

                // Frame-rate independent: the fraction remaining after dt seconds rather than a
                // fixed fraction per frame, so the feel does not change with the frame rate.
                var t = 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime);
                _direction = Vector3.Slerp(_direction, look, t).normalized;
            }

            // Rigid sideways, fixed vertically.
            //
            // Following the eye horizontally is what keeps the panel in front of you as you
            // walk, and it is not smoothed at all because it is the anchor that must not lag.
            // Following it *up and down* was a mistake of the same kind as smoothing the rest:
            // the interface has no business moving because you nodded, and it moved a long
            // way, since the eyes swing several centimetres below the neck for a modest look
            // downward. The height comes from the view's anchor instead, so the panel rises
            // with her and not with your neck.
            var eye = camera.position;
            _panel.position = new Vector3(eye.x, VrCamera.SteadyEyeHeight, eye.z)
                            + _direction * cfg.HudDistance.Value
                            + Vector3.up * cfg.HudHeightOffset.Value;
            _panel.rotation = Quaternion.LookRotation(_direction, Vector3.up);

            var width = cfg.HudSize.Value;
            var height = width * _texture.height / _texture.width;
            _panel.localScale = new Vector3(width, height, 1f);
        }
    }
}
