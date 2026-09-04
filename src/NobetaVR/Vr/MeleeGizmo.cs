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
    /// So the shape is drawn rather than asked for — and it can be drawn honestly, because the
    /// log settled what the shape is. The 27 attack ranges on this character carry no collider
    /// at all and all sit at the same point, so a range is a *position* and the volume around it
    /// is <c>AnimAttackCollisionData.g_fCollisionSize</c>, one radius shared by every attack.
    /// The hitbox is a sphere, and this draws that sphere at that radius: not an indication of
    /// it, the thing itself.
    ///
    /// It changes colour while the collision is open. That is the whole point of looking: a
    /// swing that misses has two quite different causes — the sphere was somewhere else, or it
    /// never opened — and from inside a headset they are the same nothing.
    /// </summary>
    internal sealed class MeleeGizmo
    {
        private Transform _sphere;
        private Material _material;
        private bool _failed;

        /// <summary>Idle: the sphere is where it will be, but no blow is live.</summary>
        private static readonly Color Idle = new(0.35f, 0.7f, 1f, 0.16f);

        /// <summary>Open: this is a blow, and anything inside is being hit.</summary>
        private static readonly Color Live = new(1f, 0.45f, 0.15f, 0.42f);

        public void Show(Vector3 centre, float radius, bool live)
        {
            if (_failed) return;
            if (_sphere == null && !Build()) return;

            _sphere.position = centre;

            // The primitive sphere is a unit diameter, so a radius is twice the scale.
            var diameter = Mathf.Max(0.02f, radius * 2f);
            _sphere.localScale = new Vector3(diameter, diameter, diameter);

            _material.color = live ? Live : Idle;

            if (!_sphere.gameObject.activeSelf) _sphere.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_sphere != null && _sphere.gameObject.activeSelf) _sphere.gameObject.SetActive(false);
        }

        private bool Build()
        {
            var shader = TransparentShader.Find();
            if (shader == null) { _failed = true; return false; }

            _material = new Material(shader);

            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "NobetaVR Melee Hitbox";
            Object.DontDestroyOnLoad(sphere);
            sphere.hideFlags = HideFlags.HideAndDontSave;

            // A primitive comes with a collider, and this one would be a ball of invisible
            // glass hanging in front of the player — the aim raycast would find it instead of
            // whatever is behind it, so a diagnostic left on would quietly change where the
            // shot goes.
            var collider = sphere.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);

            var renderer = sphere.GetComponent<MeshRenderer>();
            renderer.material = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _sphere = sphere.transform;

            // Layer 0: seen by the game's cameras, not by the HUD capture camera, whose mask is
            // built from the interface canvases alone.
            _sphere.gameObject.layer = 0;
            _sphere.gameObject.SetActive(false);

            Plugin.Log.LogInfo("melee hitbox gizmo built");
            return true;
        }
    }
}
