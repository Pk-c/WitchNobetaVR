using System;
using Il2CppInterop.Runtime;
using NobetaVR.Vr;
using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Puts the game's damage numbers in the world, where the hit was.
    ///
    /// The game already has them and they were never being drawn. <c>UIHitNumber</c> keeps a pool
    /// of <c>UIJumpNumber</c>, each one handed a world position and asked to project it into
    /// screen space every frame; the row of digits is then placed on the stage canvas at the
    /// answer. On a monitor that is exactly right. In a headset the whole of that canvas is
    /// captured onto <see cref="HudPanel"/> -- a flat quad a metre and a half in front of your
    /// eyes, covering perhaps sixty degrees of a hundred -- and a screen coordinate has no
    /// meaning on it: the projection is taken through the game camera at eye-buffer resolution,
    /// the panel is a 1920x1080 texture, and the two do not agree. So the numbers were being
    /// computed, animated and faded, frame after frame, somewhere off the edge of the only
    /// surface that could have shown them.
    ///
    /// <para>
    /// This is the same answer <see cref="AimReticle"/> gives, for the same reason: the position
    /// was the only part that was ever wrong, so the position is the only part replaced. The
    /// digits are the game's own sprites, in the game's own per-element colours, read live off
    /// <c>UINumberSprite</c> -- see <see cref="SpriteRun"/>. They are drawn at the world point
    /// the game handed us, at the depth the hit actually happened, so both eyes agree about
    /// where that is and no projection is left to get wrong.
    /// </para>
    ///
    /// <para>
    /// Size is angular rather than absolute, as everywhere else in the mod: a number that shrank
    /// with distance would be unreadable at exactly the range you are fighting at, and one that
    /// did not would be a billboard against a near wall.
    /// </para>
    ///
    /// <para>
    /// The flat ones are faded out rather than destroyed or disabled -- a <c>CanvasGroup</c> on
    /// <c>UIHitNumber</c>'s own object, which multiplies with everything the game does underneath
    /// and is given straight back the moment the setting is turned off.
    /// </para>
    /// </summary>
    public sealed class DamageNumbers : MonoBehaviour
    {
        public DamageNumbers(IntPtr ptr) : base(ptr) { }

        internal static DamageNumbers Instance { get; private set; }

        /// <summary>
        /// How many can be in the air at once. The game's own pool is sixteen
        /// (<c>UIHitNumber.HIT_NUMBER_LENGTH</c>); this is deliberately larger, because a number
        /// stolen back from a hit that is still on screen is worse than one more quad.
        /// </summary>
        private const int Pool = 24;

        /// <summary>The most digits a hit can have. The game's own row holds four.</summary>
        private const int Digits = 4;

        private readonly Number[] _numbers = new Number[Pool];
        private readonly Sprite[] _run = new Sprite[Digits];
        private readonly int[] _value = new int[Digits];

        private Transform _root;
        private bool _failed;

        private UINumberSprite _sprites;
        private UIHitNumber _flat;
        private CanvasGroup _flatGroup;
        private float _flatAlpha = 1f;
        private float _nextScan;
        private string _scene;

        private void Awake() => Instance = this;

        // -- what the game tells us ------------------------------------------------------

        /// <summary>
        /// A hit landed. Taken from <c>UIHitNumber.SetHitNumber</c> rather than watched for, so
        /// this happens on the frame the game itself decided there was a number to show, with
        /// the world position and the element it decided them with.
        /// </summary>
        internal static void Hit(int value, Vector3 position, PlayerEffectPlay.Magic element)
        {
            var self = Instance;
            if (self == null || self._failed) return;
            if (!Plugin.Instance.WorldDamageNumbers.Value) return;

            self.Spawn(value, position, element);
        }

        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private void Spawn(int value, Vector3 position, PlayerEffectPlay.Magic element)
        {
            if (_sprites == null) return;
            if (_root == null && !Build()) return;

            var camera = VrCamera.CameraTransform;
            if (camera == null) return;

            var count = Split(value);
            if (count <= 0) return;

            for (var i = 0; i < count; i++) _run[i] = Digit(_value[i], element);

            var number = Free();
            if (!number.Show(_run, count)) return;

            // The sideways scatter is fixed in the world at the moment of the hit rather than
            // taken from the camera each frame. Several hits land on the same point in quick
            // succession and would otherwise stack into one illegible smear -- the game scatters
            // them for the same reason, with its own g_v3RandomDir -- but a direction re-read
            // from a head that is turning would slide the number sideways as you look around,
            // which reads as the number being loose rather than as the hit being where it was.
            number.Begin(position, camera.right * UnityEngine.Random.Range(-1f, 1f));
        }

        /// <summary>The digits of a hit, most significant first, and how many there are.</summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private int Split(int value)
        {
            value = Mathf.Clamp(value, 0, 9999);

            if (value == 0) { _value[0] = 0; return 1; }

            var count = 0;
            for (var v = value; v > 0 && count < Digits; v /= 10) count++;

            var at = count;
            for (var v = value; v > 0 && at > 0; v /= 10) _value[--at] = v % 10;

            return count;
        }

        /// <summary>
        /// One digit, in the colour the game would have drawn it. Read through the game's own
        /// accessors rather than off the arrays, so that whatever bounds rule they apply is the
        /// one that applies here too.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private Sprite Digit(int digit, PlayerEffectPlay.Magic element) => element switch
        {
            PlayerEffectPlay.Magic.Ice => _sprites.GetIceSpriteNumber(digit),
            PlayerEffectPlay.Magic.Fire => _sprites.GetFireSpriteNumber(digit),
            PlayerEffectPlay.Magic.Lightning => _sprites.GetLightningSpriteNumber(digit),
            _ => _sprites.GetSpriteNumber(digit),
        };

        /// <summary>
        /// A free slot, or the oldest one if every slot is busy. Never null: dropping a hit on the
        /// floor because the pool is full is the one failure the player would read as the feature
        /// being broken, and the oldest number is the one they have already finished reading.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private Number Free()
        {
            var oldest = _numbers[0];

            for (var i = 0; i < _numbers.Length; i++)
            {
                var number = _numbers[i];
                if (!number.Live) return number;
                if (number.Born < oldest.Born) oldest = number;
            }

            return oldest;
        }

        // -- per frame -------------------------------------------------------------------

        private void LateUpdate()
        {
            if (_failed) return;

            Resolve();
            Flat();

            if (_root == null) return;

            // Gated on the view being hers rather than on VrHands.PlayerInControl, which is
            // what the reticle uses. The reticle is about the hands and can afford to go with
            // them; a damage number is not, and hanging it off whether a skinned hand mesh bound
            // successfully would be a fight it has no stake in. ViewIsYours answers the question
            // actually being asked -- is this gameplay or is this a cutscene -- and answers true
            // when it cannot tell, which is the right way round for something the player is
            // meant to see.
            var on = Plugin.Instance.WorldDamageNumbers.Value && VrCamera.ViewIsYours;
            var camera = VrCamera.CameraTransform;

            for (var i = 0; i < _numbers.Length; i++)
            {
                var number = _numbers[i];
                if (!number.Live) continue;

                if (!on || camera == null) { number.Stop(); continue; }

                number.Follow(camera);
            }
        }

        /// <summary>
        /// Fades the game's own row out while ours is on, and back in when it is not.
        ///
        /// Faded rather than switched off, for the reason <see cref="GameHud"/> gives: a
        /// <c>CanvasGroup</c> multiplies with whatever the game is already doing to those objects,
        /// so every one of its own animations and its own hiding rules keeps working underneath,
        /// and the setting stays reversible at runtime with nothing left behind.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private void Flat()
        {
            if (_flatGroup == null) return;

            var target = Plugin.Instance.WorldDamageNumbers.Value ? 0f : 1f;
            var step = Mathf.Max(0.01f, Plugin.Instance.HudFadeSpeed.Value) * Time.unscaledDeltaTime;

            _flatAlpha = Mathf.MoveTowards(_flatAlpha, target, step);
            _flatGroup.alpha = _flatAlpha;
        }

        // -- resolution ------------------------------------------------------------------

        /// <summary>
        /// Finds the game's digit set and its hit-number pool, on a timer and again after every
        /// scene change: both are rebuilt per stage, so a reference taken once is a dead pointer
        /// from the first loading screen onwards.
        /// </summary>
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private void Resolve()
        {
            var scene = ActiveScene.Name;
            if (scene != _scene)
            {
                _scene = scene;
                _sprites = null;
                _flat = null;
                _flatGroup = null;
                _flatAlpha = 1f;
                _nextScan = 0f;
            }

            if (_sprites != null && _flat != null) return;
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1f;

            if (_sprites == null)
            {
                var found = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<UINumberSprite>());
                _sprites = found != null ? found.TryCast<UINumberSprite>() : null;
            }

            if (_flat != null) return;

            var hits = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<UIHitNumber>());
            _flat = hits != null ? hits.TryCast<UIHitNumber>() : null;
            if (_flat == null) return;

            // The group goes on UIHitNumber's own object, which is where the pool is built. It
            // cannot be checked from here -- hitNumberElements is still empty when this runs,
            // because the scan finds the component before Init has filled it -- and it does not
            // greatly matter: a flat row that escaped the group is drawn on the panel, which is
            // where it was already invisible. The group is what makes the setting reversible,
            // not what makes the numbers go away.
            var go = _flat.gameObject;
            _flatGroup = go.GetComponent<CanvasGroup>();
            if (_flatGroup == null) _flatGroup = go.AddComponent<CanvasGroup>();
            _flatAlpha = _flatGroup.alpha;
        }

        // -- construction ----------------------------------------------------------------

        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        private bool Build()
        {
            var shader = TransparentShader.Find();
            if (shader == null)
            {
                Plugin.Log.LogError("No alpha-blended shader survived stripping, so the damage "
                                  + "numbers would be opaque slabs. Leaving them off.");
                _failed = true;
                return false;
            }

            var host = new GameObject("NobetaVR Damage Numbers");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            _root = host.transform;

            for (var i = 0; i < _numbers.Length; i++) _numbers[i] = new Number(_root, shader, i);

            return true;
        }

        // -- one number ------------------------------------------------------------------

        /// <summary>
        /// One row of digits, alive between a hit and the end of its life.
        ///
        /// A plain class rather than a component: nothing here wants a Unity message, and an
        /// injected MonoBehaviour per pooled number would be twenty-four registrations and
        /// twenty-four interop calls a frame for an Update its owner is already making.
        /// </summary>
        private sealed class Number
        {
            /// <summary>How long the pop at the front takes, as a fraction of the whole life.</summary>
            private const float PopFor = 0.18f;

            /// <summary>How much bigger the pop goes at its peak.</summary>
            private const float PopBy = 0.35f;

            /// <summary>Where in the life the fade starts, as a fraction of it.</summary>
            private const float FadeFrom = 0.55f;

            private readonly GameObject _object;
            private readonly Mesh _mesh;
            private readonly Material _material;
            private readonly Transform _transform;

            private Vector3 _origin;
            private Vector3 _scatter;
            private float _born;
            private float _width = 1f;

            internal Number(Transform parent, Shader shader, int index)
            {
                _object = new GameObject($"Number {index}") { hideFlags = HideFlags.HideAndDontSave };
                _transform = _object.transform;
                _transform.SetParent(parent, false);

                // Layer 0 and no collider, for the reason AimReticle gives: this is seen by the
                // game's own cameras, the HUD capture camera is kept off it by distance, and a
                // collider here would be a pane of invisible glass for the aim ray to find.
                _object.layer = 0;

                _mesh = new Mesh { name = $"NobetaVR Damage Number {index}", hideFlags = HideFlags.HideAndDontSave };
                _mesh.MarkDynamic();

                var filter = _object.AddComponent<MeshFilter>();
                filter.sharedMesh = _mesh;

                _material = new Material(shader) { renderQueue = 3000 };

                // Drawn over the world on purpose. A number is spawned at the point a blow
                // landed, which is on -- and often just inside -- the thing that was hit, so a
                // depth-tested row of digits is a row of digits buried in an enemy. The flat
                // game drew these on an overlay above everything, and that is the behaviour
                // being kept; only the place they are drawn at has changed.
                _material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
                _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);

                var renderer = _object.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                _object.SetActive(false);
            }

            internal bool Live => _object.activeSelf;
            internal float Born => _born;

            /// <summary>Builds the row, and says whether there was anything to build it from.</summary>
            internal bool Show(Sprite[] sprites, int count)
            {
                if (!SpriteRun.Build(_mesh, sprites, count, out var width, out var texture)) return false;

                _width = width;
                _material.mainTexture = texture;
                return true;
            }

            internal void Begin(Vector3 origin, Vector3 scatter)
            {
                _origin = origin;
                _scatter = scatter;
                _born = Time.unscaledTime;

                _object.SetActive(true);
            }

            internal void Stop()
            {
                if (_object.activeSelf) _object.SetActive(false);
            }

            /// <summary>
            /// Places and animates the row for this frame, and puts it away when its life is up.
            ///
            /// On unscaled time, like the rest of the mod's own interface. Scaled would put the
            /// number on the same clock as the impact it reports, which is the prettier answer
            /// for the hit pause -- but the game also takes that clock to zero for its pause
            /// menu, and a number caught in the air by one would hang there, unaged, for as long
            /// as the menu was open.
            /// </summary>
            internal void Follow(Transform camera)
            {
                var cfg = Plugin.Instance;

                var life = Mathf.Max(0.1f, cfg.DamageNumberLife.Value);
                var age = (Time.unscaledTime - _born) / life;

                if (age >= 1f) { Stop(); return; }

                var toward = _origin - camera.position;
                var distance = toward.magnitude;
                if (distance < 0.05f) { Stop(); return; }

                // Angular, so the row reads the same at every range; the height is the unit
                // SpriteRun normalised to, so this setting is the height of a digit.
                var size = distance * Mathf.Max(0.001f, cfg.DamageNumberSize.Value);

                // Rise eased out: fast off the hit, settling as it fades, which is what makes the
                // number read as thrown off the blow rather than as floating up from nothing.
                var rise = cfg.DamageNumberRise.Value * (1f - (1f - age) * (1f - age));
                var drift = cfg.DamageNumberSpread.Value * age;

                var pop = 1f + PopBy * Mathf.Sin(Mathf.Clamp01(age / PopFor) * Mathf.PI);
                var alpha = age < FadeFrom ? 1f : 1f - (age - FadeFrom) / (1f - FadeFrom);

                _transform.position = _origin + camera.up * (rise * size) + _scatter * (drift * size * _width);
                _transform.rotation = Quaternion.LookRotation(toward / distance, camera.up);
                _transform.localScale = Vector3.one * (size * pop);

                _material.color = new Color(1f, 1f, 1f, alpha);
            }
        }
    }
}
