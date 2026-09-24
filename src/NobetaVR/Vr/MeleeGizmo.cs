using NobetaVR.Ui;
using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Draws the melee hitbox, because the game's own switch for it does not.
    ///
    /// <c>AnimAttackCollision.g_bShowRange</c> looks exactly like the setting wanted here, and
    /// in the editor it is: range drawing in Unity goes through <c>OnDrawGizmos</c>, and gizmos
    /// are editor-only. The flag survives into the shipped build because it is an ordinary
    /// field; the code that read it does not. Setting it did nothing, and nothing is all it can
    /// ever do here.
    ///
    /// So the shape is drawn rather than asked for. What is drawn is the capsule the mod means
    /// by a blow — the axis <see cref="VrMelee"/> lays on the wand, thickened by the radius it
    /// writes into <c>g_fCollisionSize</c> — and not the sphere the game will actually test,
    /// which spends every frame somewhere inside it. The volume is the honest picture: anything
    /// in it is hit, and where in it the sphere happened to be is an implementation detail of
    /// how the capsule is served.
    ///
    /// Three primitives rather than one stretched capsule. A Unity capsule scaled along its
    /// length pulls its end caps into ellipsoids, so it would draw a shape that is not the one
    /// being tested — at which point a diagnostic is lying about the thing it exists to show.
    /// A cylinder with a sphere on each end is the shape.
    ///
    /// It changes colour while the collision is open. That is the whole point of looking: a
    /// swing that misses has two quite different causes — the capsule was somewhere else, or it
    /// never opened — and from inside a headset they are the same nothing.
    /// </summary>
    internal sealed class MeleeGizmo
    {
        private Transform _capFrom;
        private Transform _capTo;
        private Transform _shaft;
        private Material _material;
        private bool _failed;

        /// <summary>Idle: the capsule is where it will be, but no blow is live.</summary>
        private static readonly Color Idle = new(0.35f, 0.7f, 1f, 0.16f);

        /// <summary>Open: this is a blow, and anything inside is being hit.</summary>
        private static readonly Color Live = new(1f, 0.45f, 0.15f, 0.42f);

        public void Show(Vector3 from, Vector3 to, float radius, bool live)
        {
            if (_failed) return;
            if (_capFrom == null && !Build()) return;

            var diameter = Mathf.Max(0.02f, radius * 2f);
            var thickness = new Vector3(diameter, diameter, diameter);

            _capFrom.position = from;
            _capFrom.localScale = thickness;
            _capTo.position = to;
            _capTo.localScale = thickness;

            var span = to - from;
            var length = span.magnitude;

            // The primitive cylinder is two units tall about its own centre and lies along its
            // local Y, so a length is half the scale and the rotation puts that Y on the axis.
            if (length > 1e-4f)
            {
                _shaft.position = (from + to) * 0.5f;
                _shaft.rotation = Quaternion.FromToRotation(Vector3.up, span / length);
                _shaft.localScale = new Vector3(diameter, length * 0.5f, diameter);
                Enable(_shaft, true);
            }
            else
            {
                // Length zero is the game's own shape, a plain sphere, and the two caps are
                // already sitting on top of each other drawing it.
                Enable(_shaft, false);
            }

            _material.color = live ? Live : Idle;

            Enable(_capFrom, true);
            Enable(_capTo, true);
        }

        public void Hide()
        {
            Enable(_capFrom, false);
            Enable(_capTo, false);
            Enable(_shaft, false);
        }

        private static void Enable(Transform t, bool on)
        {
            if (t != null && t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
        }

        private bool Build()
        {
            var shader = TransparentShader.Find();
            if (shader == null) { _failed = true; return false; }

            _material = new Material(shader);

            _capFrom = Piece(PrimitiveType.Sphere, "NobetaVR Melee Hitbox Cap A");
            _capTo = Piece(PrimitiveType.Sphere, "NobetaVR Melee Hitbox Cap B");
            _shaft = Piece(PrimitiveType.Cylinder, "NobetaVR Melee Hitbox Shaft");

            return true;
        }

        private Transform Piece(PrimitiveType type, string name)
        {
            var piece = GameObject.CreatePrimitive(type);
            piece.name = name;
            Object.DontDestroyOnLoad(piece);
            piece.hideFlags = HideFlags.HideAndDontSave;

            // A primitive comes with a collider, and this one would be invisible glass hanging
            // in front of the player — the aim raycast would find it instead of whatever is
            // behind it, so a diagnostic left on would quietly change where the shot goes. The
            // melee capsule's own overlap test would find it too, and aim the blow at itself.
            var collider = piece.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);

            var renderer = piece.GetComponent<MeshRenderer>();
            renderer.material = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // Layer 0: seen by the game's cameras, not by the HUD capture camera, whose mask is
            // built from the interface canvases alone. See HudPanel.Park.
            piece.layer = 0;
            piece.SetActive(false);

            return piece.transform;
        }
    }
}
