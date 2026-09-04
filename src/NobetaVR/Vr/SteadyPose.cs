using UnityEngine;

namespace NobetaVR.Vr
{
    /// <summary>
    /// Takes the tremor out of a tracked pose without putting lag into it.
    ///
    /// The hands never looked like they were shaking; the reticle did. That is not two bugs, it
    /// is one, seen through a lever: the mark sits at the end of a ray, so a tenth of a degree
    /// of noise at the controller is invisible on the hand and four centimetres of wobble at
    /// fifteen metres. Anything that fixes it has to fix it at the controller, before the aim
    /// is taken from it, or the hand and the shot stop agreeing.
    ///
    /// A plain low-pass would do it and would be the wrong answer. Smoothing enough to settle a
    /// tremor is smoothing you feel the moment you move, because a fixed filter cannot tell
    /// noise from a hand: it is slow for both. That trade is the whole problem, and the
    /// "1€ filter" (Casiez, Roussel and Vogel, 2012) is the standard way out of it — the cutoff
    /// is not fixed but rides the speed of the signal. Held still, the cutoff falls and the
    /// tremor goes; moved, it rises with the hand and the filter all but disappears. Lag is
    /// then only ever spent where nothing is happening to be late for.
    ///
    /// One knob is exposed, and it sets how low the cutoff falls when the hand is at rest.
    /// The speed term is left alone: that is the part that keeps this from being felt, and it
    /// is not a matter of taste.
    /// </summary>
    internal sealed class SteadyPose
    {
        /// <summary>
        /// Cutoff in Hz at each end of the steadiness dial, interpolated geometrically because
        /// this is a frequency: the useful settings are bunched at the bottom, and stepping a
        /// slider linearly through 12 Hz would spend most of its travel doing nothing.
        /// </summary>
        private const float CutoffOff = 12f;
        private const float CutoffFull = 0.4f;

        /// <summary>
        /// How hard speed lifts the cutoff, per unit per second. Two values because the two
        /// signals are not in the same units: metres for the position, quaternion components
        /// for the rotation, whose rate for a wrist turning at a normal speed happens to land
        /// within a factor of two of a hand moving at a normal speed.
        /// </summary>
        private const float SpeedLiftPosition = 12f;
        private const float SpeedLiftRotation = 6f;

        private readonly Channel _px = new(), _py = new(), _pz = new();
        private readonly Channel _rx = new(), _ry = new(), _rz = new(), _rw = new();

        private Quaternion _previous = Quaternion.identity;
        private bool _started;

        /// <summary>
        /// Forgets everything, so the next pose is taken as it is rather than smoothed towards
        /// from wherever the hand was when it was last drawn. Used when the hands are put away
        /// and brought back: a cutscene is long enough that the two poses have nothing to do
        /// with each other, and filtering between them would slide the hand into place.
        /// </summary>
        public void Reset()
        {
            _px.Reset(); _py.Reset(); _pz.Reset();
            _rx.Reset(); _ry.Reset(); _rz.Reset(); _rw.Reset();
            _started = false;
        }

        /// <param name="steadiness">0 to 1; 0 is no filtering at all.</param>
        /// <param name="response">Multiplier on how readily the filter lets go when the hand
        /// moves. One is the tuned value; there is rarely a reason to change it.</param>
        public void Apply(ref Vector3 position, ref Quaternion rotation,
                          float dt, float steadiness, float response)
        {
            var cutoff = CutoffOff * Mathf.Pow(CutoffFull / CutoffOff, Mathf.Clamp01(steadiness));
            var liftPosition = SpeedLiftPosition * response;
            var liftRotation = SpeedLiftRotation * response;

            position = new Vector3(
                _px.Filter(position.x, dt, cutoff, liftPosition),
                _py.Filter(position.y, dt, cutoff, liftPosition),
                _pz.Filter(position.z, dt, cutoff, liftPosition));

            // A quaternion and its negation are the same rotation, and the components are
            // filtered one at a time with no idea of that. Left alone, the frame a controller's
            // pose crosses over reads as all four components leaping to their opposites, which
            // is the largest possible step in the signal and takes a quarter of a second to
            // settle out of. Keeping successive samples on one side of the sphere costs a dot
            // product.
            if (_started && Quaternion.Dot(_previous, rotation) < 0f)
                rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);

            _started = true;
            _previous = rotation;

            var x = _rx.Filter(rotation.x, dt, cutoff, liftRotation);
            var y = _ry.Filter(rotation.y, dt, cutoff, liftRotation);
            var z = _rz.Filter(rotation.z, dt, cutoff, liftRotation);
            var w = _rw.Filter(rotation.w, dt, cutoff, liftRotation);

            // Normalised by hand rather than through Quaternion.Normalize: this build is IL2CPP
            // and what the game never calls is not there to call. The same wall as
            // TryGetFeatureValue.
            var length = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (length > 0.0001f)
                rotation = new Quaternion(x / length, y / length, z / length, w / length);
        }

        /// <summary>
        /// One number, filtered. The cutoff is recomputed every sample from how fast the value
        /// is moving, which is the whole of the idea.
        /// </summary>
        private sealed class Channel
        {
            /// <summary>
            /// Cutoff for the derivative's own filter, in Hz. Fixed, and low: the derivative is
            /// noisier than the signal, and a jittery speed estimate would open the main filter
            /// on noise alone, which is the failure that makes this behave like no filter.
            /// </summary>
            private const float DerivativeCutoff = 1f;

            private float _value;
            private float _raw;
            private float _speed;
            private bool _started;

            public void Reset() => _started = false;

            public float Filter(float raw, float dt, float cutoff, float lift)
            {
                if (!_started || dt <= 0f)
                {
                    _started = true;
                    _value = _raw = raw;
                    _speed = 0f;
                    return raw;
                }

                var rate = (raw - _raw) / dt;
                _raw = raw;
                _speed = Mathf.Lerp(_speed, rate, Alpha(DerivativeCutoff, dt));

                _value = Mathf.Lerp(_value, raw, Alpha(cutoff + lift * Mathf.Abs(_speed), dt));
                return _value;
            }

            /// <summary>
            /// The lerp factor a one-pole low-pass at this cutoff needs over this timestep.
            /// Derived rather than tuned, which is what makes the filter behave the same at 72
            /// and at 120 frames a second.
            /// </summary>
            private static float Alpha(float cutoff, float dt)
            {
                var tau = 1f / (2f * Mathf.PI * cutoff);
                return 1f / (1f + tau / dt);
            }
        }
    }
}
