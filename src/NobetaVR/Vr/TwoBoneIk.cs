using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// An analytic two-bone IK solver, for arms.
    ///
    /// The mod has to bring its own. The build ships FinalIK, but only the solvers the game
    /// itself uses: an <c>AimIK</c> for the wand, a <c>LookAtIK</c> for the gaze and a
    /// <c>CCDIK</c> for the hair. There is no limb solver anywhere in it.
    ///
    /// The elbow is placed explicitly rather than reached by correcting angles. The first
    /// version did the latter — measure the current shoulder and elbow angles, work out the
    /// wanted ones from the law of cosines, and rotate by the difference about the current bend
    /// plane. The arithmetic was right and it still mangled the arm, because the bend plane is
    /// taken from a cross product of two nearly parallel vectors whenever the arm is nearly
    /// straight, which is exactly how Nobeta stands at rest. The magnitude guard did not save
    /// it: the cross product was not zero, it was small, and a small cross product has a
    /// perfectly finite length and a direction that is entirely numerical noise. The arm was
    /// then rotated by a large angle about a random axis.
    ///
    /// Placing the elbow needs no such plane. The triangle is solved directly, the elbow's
    /// distance from the shoulder line is a length rather than an angle, and the only direction
    /// involved is the pole — supplied by the caller, and chosen so it can never be parallel to
    /// the arm.
    /// </summary>
    internal static class TwoBoneIk
    {
        /// <summary>
        /// Bends an arm so the hand lands on the target.
        ///
        /// Must run after the animator has posed the skeleton: the solve corrects the current
        /// pose rather than replacing it, so anything writing these bones later simply wins.
        /// </summary>
        /// <param name="upper">Upper arm bone.</param>
        /// <param name="fore">Forearm bone. Must be a descendant of <paramref name="upper"/>.</param>
        /// <param name="hand">Hand bone. Must be a descendant of <paramref name="fore"/>.</param>
        /// <param name="target">Where the hand should be.</param>
        /// <param name="handRotation">How the hand should be oriented.</param>
        /// <param name="pole">
        /// Which way the elbow should point, as a direction from the shoulder. It only has to be
        /// roughly right — it selects one of the infinitely many elbow positions on the circle
        /// the triangle allows — but it must not be parallel to the shoulder-to-target line.
        /// </param>
        public static void Solve(Transform upper, Transform fore, Transform hand,
                                 Vector3 target, Quaternion handRotation, Vector3 pole,
                                 Quaternion handRestRelativeToFore, float twistShare)
        {
            if (upper == null || fore == null || hand == null) return;

            var shoulder = upper.position;
            var upperLength = Vector3.Distance(shoulder, fore.position);
            var foreLength = Vector3.Distance(fore.position, hand.position);
            if (upperLength < 1e-4f || foreLength < 1e-4f) return;

            var toTarget = target - shoulder;
            var reach = toTarget.magnitude;
            if (reach < 1e-4f) return;

            var direction = toTarget / reach;

            // Just short of straight and just past folded. At either limit the triangle
            // collapses and the elbow's position stops being defined by the pole.
            reach = Mathf.Clamp(reach,
                Mathf.Abs(upperLength - foreLength) + 1e-3f,
                (upperLength + foreLength) * 0.999f);

            // The elbow, in the plane containing the shoulder-target line and the pole:
            // `along` is how far down that line it sits, `out` how far off it.
            var along = (upperLength * upperLength - foreLength * foreLength + reach * reach)
                      / (2f * reach);
            var offset = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));

            // Only the part of the pole perpendicular to the arm matters; the parallel part
            // would just slide the elbow along the line it is already on.
            var perpendicular = pole - Vector3.Project(pole, direction);
            if (perpendicular.sqrMagnitude < 1e-6f)
            {
                // The caller handed us a pole pointing straight down the arm. Any perpendicular
                // will do rather than none, and this is at least deterministic.
                perpendicular = Vector3.Cross(direction, Vector3.up);
                if (perpendicular.sqrMagnitude < 1e-6f) perpendicular = Vector3.Cross(direction, Vector3.forward);
            }
            perpendicular.Normalize();

            var elbow = shoulder + direction * along + perpendicular * offset;

            // Aim each bone at where the next one now belongs. Reading the positions again
            // between the two steps matters: rotating the upper arm has already carried the
            // forearm and the hand with it.
            upper.rotation = Quaternion.FromToRotation(fore.position - shoulder, elbow - shoulder)
                           * upper.rotation;

            fore.rotation = Quaternion.FromToRotation(hand.position - fore.position, target - fore.position)
                          * fore.rotation;

            DistributeTwist(fore, hand, handRotation, handRestRelativeToFore, twistShare);

            hand.rotation = handRotation;
        }

        /// <summary>
        /// Gives the forearm its share of the wrist's roll.
        ///
        /// A real forearm carries pronation along its whole length — the radius rolls about the
        /// ulna — so turning a palm over rotates the arm from the elbow down. A rig with one
        /// forearm bone has nowhere to put that, and rotating only the hand piles the entire
        /// twist onto a single joint: the wrist shears, and the mesh either wrings or tears.
        ///
        /// Splitting it is what makes the join read as a wrist rather than a break. Only the
        /// roll is moved, taken by decomposing the wanted rotation about the forearm's own axis
        /// — the swing has to stay on the hand, or the palm would stop facing where the
        /// controller points. Rotating about that axis also leaves the hand exactly where the
        /// solver put it, since the hand sits on the line being rotated about.
        /// </summary>
        private static void DistributeTwist(Transform fore, Transform hand, Quaternion wanted,
                                            Quaternion restRelativeToFore, float share)
        {
            if (share <= 0f) return;

            var rollAxis = hand.position - fore.position;
            if (rollAxis.sqrMagnitude < 1e-8f) return;
            rollAxis.Normalize();

            // Where the hand would sit if the forearm were not twisted at all.
            var neutral = fore.rotation * restRelativeToFore;
            var delta = wanted * Quaternion.Inverse(neutral);

            var twist = TwistAbout(delta, rollAxis);
            twist.ToAngleAxis(out var angle, out var axis);
            if (float.IsNaN(angle) || angle < 1e-3f) return;

            if (angle > 180f) angle -= 360f;
            if (Vector3.Dot(axis, rollAxis) < 0f) angle = -angle;

            fore.rotation = Quaternion.AngleAxis(angle * share, rollAxis) * fore.rotation;
        }

        /// <summary>
        /// The part of a rotation that turns about the given axis — the standard swing-twist
        /// decomposition, keeping only the twist.
        /// </summary>
        private static Quaternion TwistAbout(Quaternion rotation, Vector3 axis)
        {
            var rotationAxis = new Vector3(rotation.x, rotation.y, rotation.z);
            var projected = Vector3.Project(rotationAxis, axis);

            var twist = new Quaternion(projected.x, projected.y, projected.z, rotation.w);
            var magnitude = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y
                                     + twist.z * twist.z + twist.w * twist.w);

            // The rotation was a pure swing: there is no twist to take.
            if (magnitude < 1e-6f) return Quaternion.identity;

            return new Quaternion(twist.x / magnitude, twist.y / magnitude,
                                  twist.z / magnitude, twist.w / magnitude);
        }
    }
}
