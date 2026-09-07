using UnityEngine;

namespace NobetaVR.Ui
{
    /// <summary>
    /// Which way "in front of you" points, for the panels that hang there.
    ///
    /// Flattening the view's forward vector is the obvious way to ask, and it fails exactly
    /// where a nod puts you. Two thirds of the way to looking at your own feet there is almost
    /// nothing horizontal left in <c>forward</c> — at 70 degrees down it is a third of its
    /// length, at 85 a twelfth — so what survives the flattening is largely whatever roll and
    /// tracking noise the head is carrying, divided by the cosine of the pitch. The panel's
    /// yaw then rides on that noise, amplified most at the moment the head is moving fastest,
    /// which reads as the interface shivering whenever you look down.
    ///
    /// <c>up</c> answers it, because it is horizontal exactly when <c>forward</c> is not: look
    /// straight down and the head's up vector points along the direction you were facing.
    /// Weighting the two by how vertical the gaze is takes the sound half of each, and because
    /// the weight on <c>up</c> passes through zero as it changes sign, the two join without a
    /// seam.
    /// </summary>
    internal static class ViewAnchor
    {
        /// <summary>
        /// The view's yaw, as a horizontal unit vector.
        /// </summary>
        /// <param name="fallback">Returned if the pose is too degenerate to say, which needs a
        /// rotation with neither a forward nor an up worth speaking of — so, in practice,
        /// never. Pass the direction already in use, so a bad frame holds rather than jumps.
        /// </param>
        public static Vector3 YawForward(Transform view, Vector3 fallback)
        {
            var f = view.forward;
            var u = view.up;

            // The flattened gaze is worth (1 - |f.y|) and the flattened head-up is worth -f.y.
            // Signed, so the second term points along the gaze whether the head is pitched down
            // or up, and is worth nothing at all while the gaze is level.
            var level = 1f - Mathf.Abs(f.y);
            var yaw = new Vector3(f.x * level - u.x * f.y, 0f, f.z * level - u.z * f.y);

            return yaw.sqrMagnitude > 0.0001f ? yaw.normalized : fallback;
        }
    }
}
