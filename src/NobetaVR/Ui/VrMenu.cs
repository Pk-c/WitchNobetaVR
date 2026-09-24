using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using NobetaVR.Input;
using NobetaVR.Vr;
using UnityEngine;
using UnityEngine.UI;

namespace NobetaVR.Ui
{
    /// <summary>
    /// The mod's own settings, in the headset, on a panel you can read while wearing it.
    ///
    /// Built as a world-space canvas rather than captured like the game's interface. Nothing
    /// here exists on the flat screen to capture, and a world-space canvas renders in stereo by
    /// itself, at full resolution, with no texture in the middle.
    ///
    /// The whole page is one text block with the selected line marked, rather than a widget per
    /// setting. It reads the same, it cannot be mislaid by a layout group, and it means the menu
    /// needs exactly two objects on screen instead of one per row.
    ///
    /// The list is longer than a panel you can read without moving your head, so only a window
    /// of it is drawn, and the panel is sized to what was drawn. See <see cref="Redraw"/>.
    /// </summary>
    public sealed class VrMenu : MonoBehaviour
    {
        public VrMenu(IntPtr ptr) : base(ptr) { }

        private sealed class Item
        {
            public string Label;
            public Func<string> Value;
            public Action<int> Adjust;   // -1 or +1; null for a heading
            public Action Activate;      // for items that do something rather than hold a value
            public bool IsHeading;
        }

        private readonly List<Item> _items = new();
        private int _selected;

        /// <summary>Index of the first row drawn; see <see cref="EnsureVisible"/>.</summary>
        private int _scroll;

        /// <summary>The page as last drawn, so an unchanged one costs nothing to redraw.</summary>
        private string _drawn;

        /// <summary>The buffer the page is built in, kept across frames rather than remade.</summary>
        private readonly StringBuilder _page = new();

        /// <summary>Column the values line up in, in characters. Labels longer than it run on.</summary>
        private const int LabelWidth = 22;

        // How much of the list is on the panel at once, and the panel width it is drawn into.
        // The row count is what a comfortable panel holds at this font size; the height that
        // follows from it is measured rather than assumed.
        private const int VisibleRows = 16;
        private const float PanelWidth = 900f;
        private const float Padding = 40f;

        private GameObject _root;
        private Text _text;
        private bool _open;
        private bool _failed;

        private bool _toggleHeld;
        private Vector2 _lastStick;
        private float _repeatAt;

        public bool IsOpen => _open;

        internal static VrMenu Instance { get; private set; }

        private void Start()
        {
            Instance = this;
            BuildItems();
        }

        private void Update()
        {
            if (_failed) return;

            var controls = VrControls.Instance;
            if (controls == null) return;

            HandleToggle(controls);
            if (!_open) return;

            HandleNavigation(controls);
            Redraw();
        }

        // -- opening -------------------------------------------------------------------

        /// <summary>
        /// Both sticks clicked together. Checked before the individual bindings elsewhere get a
        /// look in, so that opening the menu does not also recentre you.
        /// </summary>
        private void HandleToggle(VrControls controls)
        {
            var both = controls.Input.Pressed(VrInput.Hand.Left, VrInput.Button.StickClick)
                    && controls.Input.Pressed(VrInput.Hand.Right, VrInput.Button.StickClick);

            if (both && !_toggleHeld) Toggle(controls);
            _toggleHeld = both;
        }

        private void Toggle(VrControls controls)
        {
            if (!_open && _root == null && !Build()) return;

            _open = !_open;
            if (_root != null) _root.SetActive(_open);

            // A trigger already held when the panel comes up is not a page turn on it. The same
            // seeding the game's own menus do, for the same reason.
            if (_open)
            {
                _pageLeftHeld = controls.Input.Pressed(VrInput.Hand.Left, VrInput.Button.Trigger);
                _pageRightHeld = controls.Input.Pressed(VrInput.Hand.Right, VrInput.Button.Trigger);
                _activateHeld = controls.Input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary);
                Redraw();
            }
        }

        // -- input ---------------------------------------------------------------------

        private void HandleNavigation(VrControls controls)
        {
            var stick = controls.Input.LeftStick;
            var step = MenuStick.Step(stick);

            if (step.y != 0)
            {
                if (Repeat(stick)) MoveSelection(step.y > 0 ? -1 : 1);
            }
            else if (step.x != 0)
            {
                if (Repeat(stick)) _items[_selected].Adjust?.Invoke(step.x);
            }
            else
            {
                _lastStick = Vector2.zero;
            }

            // A activates; the same button the game uses to confirm.
            if (controls.Input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary) && !_activateHeld)
                _items[_selected].Activate?.Invoke();
            _activateHeld = controls.Input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary);

            // The triggers turn pages, as they do in the game's own menus. This list has no
            // pages of its own, so its sections are the pages: they are what the list is
            // already divided into, and jumping by a screenful instead would put you somewhere
            // with nothing on screen saying which setting you had landed among.
            var left = controls.Input.Pressed(VrInput.Hand.Left, VrInput.Button.Trigger);
            var right = controls.Input.Pressed(VrInput.Hand.Right, VrInput.Button.Trigger);

            if (left && !_pageLeftHeld) Page(-1);
            if (right && !_pageRightHeld) Page(1);

            _pageLeftHeld = left;
            _pageRightHeld = right;
        }

        private bool _activateHeld;
        private bool _pageLeftHeld, _pageRightHeld;

        /// <summary>The rows the titled headings sit on: where each section starts.</summary>
        private readonly List<int> _sections = new();

        /// <summary>
        /// Moves to the first setting of the section before or after this one.
        ///
        /// The heading is put at the top of the window rather than the selection being scrolled
        /// to with its usual margin, so a page turn reads as a page: you land on the first
        /// setting of the section with its title above it saying where you are.
        /// </summary>
        private void Page(int delta)
        {
            if (_sections.Count == 0) return;

            var current = 0;
            for (var i = 0; i < _sections.Count; i++)
                if (_sections[i] <= _selected) current = i;

            var heading = _sections[(current + delta + _sections.Count) % _sections.Count];

            _selected = heading;
            for (var i = 0; i < _items.Count; i++)
            {
                _selected = (_selected + 1) % _items.Count;
                if (!_items[_selected].IsHeading) break;
            }

            _scroll = Mathf.Clamp(heading, 0, Mathf.Max(0, _items.Count - VisibleRows));
        }

        private bool Repeat(Vector2 stick)
        {
            var fresh = _lastStick.sqrMagnitude < 0.25f;
            _lastStick = stick;

            if (fresh)
            {
                _repeatAt = Time.unscaledTime + 0.35f;
                return true;
            }

            if (Time.unscaledTime < _repeatAt) return false;
            _repeatAt = Time.unscaledTime + 0.10f;
            return true;
        }

        private void MoveSelection(int delta)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                _selected = (_selected + delta + _items.Count) % _items.Count;
                if (!_items[_selected].IsHeading) break;   // headings are never selectable
            }

            EnsureVisible();
        }

        /// <summary>
        /// Scrolls the window so the selection is inside it, keeping a couple of rows of context
        /// beyond it: the next setting is on the panel before you arrive at it, so the list reads
        /// as one page moving under a cursor rather than a cursor that shunts the page a screen
        /// at a time whenever it reaches an edge. The clamp is what stops it scrolling past the
        /// last row into empty panel.
        /// </summary>
        private void EnsureVisible()
        {
            const int margin = 2;

            if (_selected < _scroll + margin)
                _scroll = _selected - margin;
            else if (_selected >= _scroll + VisibleRows - margin)
                _scroll = _selected - VisibleRows + 1 + margin;

            _scroll = Mathf.Clamp(_scroll, 0, Mathf.Max(0, _items.Count - VisibleRows));
        }

        // -- content -------------------------------------------------------------------

        private void BuildItems()
        {
            var cfg = Plugin.Instance;

            _items.Clear();
            _items.Add(new Item { Label = "CONTROL", IsHeading = true });

            _items.Add(new Item
            {
                Label = "Turn mode",
                Value = () => cfg.SmoothTurn.Value ? "Smooth" : "Snap",
                Adjust = _ => cfg.SmoothTurn.Value = !cfg.SmoothTurn.Value,
            });
            _items.Add(new Item
            {
                Label = "Snap turn angle",
                Value = () => $"{cfg.SnapTurnDegrees.Value:F0}°",
                Adjust = d => cfg.SnapTurnDegrees.Value = Mathf.Clamp(cfg.SnapTurnDegrees.Value + d * 5f, 5f, 180f),
            });
            _items.Add(new Item
            {
                Label = "Spell wheel speed",
                Value = () => $"×{cfg.SpellWheelSpeed.Value:F1}",
                Adjust = d => cfg.SpellWheelSpeed.Value =
                    Mathf.Clamp(cfg.SpellWheelSpeed.Value + d * 0.5f, 0.25f, 8f),
            });
            _items.Add(new Item
            {
                Label = "Smooth turn speed",
                Value = () => $"{cfg.SmoothTurnSpeed.Value:F0}°/s",
                Adjust = d => cfg.SmoothTurnSpeed.Value = Mathf.Clamp(cfg.SmoothTurnSpeed.Value + d * 10f, 20f, 360f),
            });

            _items.Add(new Item { Label = "", IsHeading = true });
            _items.Add(new Item { Label = "HEAD & BODY", IsHeading = true });

            _items.Add(Axis("Head offset X", () => cfg.HeadOffsetX));
            _items.Add(Axis("Head offset Y", () => cfg.HeadOffsetY));
            _items.Add(Axis("Head offset Z", () => cfg.HeadOffsetZ));

            _items.Add(new Item
            {
                Label = "Head hide distance",
                Value = () => $"{cfg.HeadHideDistance.Value:F2} m",
                Adjust = d => cfg.HeadHideDistance.Value =
                    Mathf.Clamp(cfg.HeadHideDistance.Value + d * 0.01f, 0f, 1f),
            });
            _items.Add(new Item
            {
                Label = "Body visible",
                Value = () => cfg.HideBody.Value ? "Hidden" : "Visible",
                Adjust = _ => cfg.HideBody.Value = !cfg.HideBody.Value,
            });

            _items.Add(new Item { Label = "", IsHeading = true });
            _items.Add(new Item { Label = "AIM", IsHeading = true });

            _items.Add(new Item
            {
                Label = "Aim from",
                Value = () => cfg.AimFromHand.Value ? "Wand hand" : "Gaze",
                Adjust = _ => cfg.AimFromHand.Value = !cfg.AimFromHand.Value,
            });
            _items.Add(Degrees("Wand pitch", () => cfg.AimPitchOffset));
            _items.Add(Degrees("Wand yaw", () => cfg.AimYawOffset));
            _items.Add(Degrees("Wand roll", () => cfg.AimRollOffset));

            _items.Add(new Item { Label = "", IsHeading = true });
            _items.Add(new Item { Label = "HAPTICS", IsHeading = true });

            _items.Add(new Item
            {
                Label = "Rumble",
                Value = () => cfg.Haptics.Value ? "On" : "Off",
                Adjust = _ => cfg.Haptics.Value = !cfg.Haptics.Value,
            });
            _items.Add(new Item
            {
                Label = "Rumble hand",
                Value = () => cfg.HapticsHand.Value switch
                {
                    Vr.HapticsHands.Motors => "Heavy left",
                    Vr.HapticsHands.Left => "Left",
                    Vr.HapticsHands.Right => "Right",
                    _ => "Both",
                },
                Adjust = d => cfg.HapticsHand.Value = Cycle(cfg.HapticsHand.Value, d),
            });
            _items.Add(new Item
            {
                Label = "Rumble strength",
                Value = () => $"×{cfg.HapticsStrength.Value:F2}",
                Adjust = d => cfg.HapticsStrength.Value =
                    Mathf.Clamp(cfg.HapticsStrength.Value + d * 0.05f, 0f, 2f),
            });
            _items.Add(new Item
            {
                Label = "Weakest rumble",
                Value = () => $"{cfg.HapticsMinAmplitude.Value:F2}",
                Adjust = d => cfg.HapticsMinAmplitude.Value =
                    Mathf.Clamp(cfg.HapticsMinAmplitude.Value + d * 0.01f, 0f, 0.5f),
            });
            _items.Add(new Item
            {
                Label = "Rumble pitch",
                Value = () => cfg.HapticsFrequency.Value <= 0f
                    ? "Runtime"
                    : $"{cfg.HapticsFrequency.Value:F0} Hz",
                Adjust = d => cfg.HapticsFrequency.Value =
                    Mathf.Clamp(cfg.HapticsFrequency.Value + d * 10f, 0f, 320f),
            });
            _items.Add(new Item
            {
                Label = "Test rumble",
                Value = () => "(A)",
                Activate = Vr.VrHaptics.Test,
            });

            _items.Add(new Item { Label = "", IsHeading = true });
            _items.Add(new Item { Label = "DIAGNOSTICS", IsHeading = true });

            _items.Add(new Item
            {
                Label = "FPS counter",
                Value = () => cfg.ShowFpsCounter.Value ? "On" : "Off",
                Adjust = _ => cfg.ShowFpsCounter.Value = !cfg.ShowFpsCounter.Value,
            });
            _items.Add(new Item
            {
                Label = "Frame cap",
                Value = () => FrameCapLabel(cfg.FrameRateLimit.Value),
                Adjust = d => cfg.FrameRateLimit.Value = StepFrameCap(cfg.FrameRateLimit.Value, d),
            });

            _items.Add(new Item { Label = "", IsHeading = true });
            _items.Add(new Item { Label = "CHEATS", IsHeading = true });

            _items.Add(new Item
            {
                Label = "Spell damage",
                Value = () => $"×{cfg.SpellDamageMultiplier.Value:F1}",
                Adjust = d => cfg.SpellDamageMultiplier.Value = Mathf.Clamp(
                    cfg.SpellDamageMultiplier.Value + d * 0.5f,
                    SpellDamageCheat.MinMultiplier, SpellDamageCheat.MaxMultiplier),
            });

            _items.Add(new Item { Label = "", IsHeading = true });
            _items.Add(new Item
            {
                Label = "Reset to default",
                Value = () => "(A)",
                Activate = ResetToDefault,
            });

            CollectSections();

            MoveSelection(1);   // land on the first real item rather than a heading
        }

        /// <summary>
        /// Where each section starts, for the page turn.
        ///
        /// A titled heading is a section; the blank ones are the space above it and belong to
        /// nothing. Collected once with the list rather than searched for on every page turn,
        /// and from the list itself rather than written out beside it, so a section added to
        /// <see cref="BuildItems"/> is a section the triggers reach without anything else being
        /// remembered.
        /// </summary>
        private void CollectSections()
        {
            _sections.Clear();

            for (var i = 0; i < _items.Count; i++)
                if (_items[i].IsHeading && !string.IsNullOrEmpty(_items[i].Label))
                    _sections.Add(i);
        }

        /// <summary>
        /// The frame caps this row offers, in the order it steps through them.
        ///
        /// Zero leaves the game's own limit alone and -1 is Unity's own "no limit". The rest
        /// are the rates headsets actually run at, because the only cap worth choosing is one
        /// that clears the one you are wearing — below it you are choosing the judder.
        /// </summary>
        private static readonly int[] FrameCaps = { 0, 72, 90, 120, 144, 240, -1 };

        private static string FrameCapLabel(int fps) =>
            fps == 0 ? "The game's" : fps < 0 ? "Unlimited" : $"{fps} fps";

        private static int StepFrameCap(int fps, int direction)
        {
            var index = Array.IndexOf(FrameCaps, fps);

            // Not one of the offers, so someone typed a number into the config file. Snap to
            // the nearest of them rather than pretend the list holds it.
            if (index < 0)
            {
                index = 0;
                for (var i = 1; i < FrameCaps.Length; i++)
                    if (Mathf.Abs(FrameCaps[i] - fps) < Mathf.Abs(FrameCaps[index] - fps)) index = i;
                return FrameCaps[index];
            }

            return FrameCaps[Mathf.Clamp(index + direction, 0, FrameCaps.Length - 1)];
        }

        private static Item Degrees(string label, Func<BepInEx.Configuration.ConfigEntry<float>> entry) => new()
        {
            Label = label,
            Value = () => $"{entry().Value:F0}°",
            Adjust = d => entry().Value = Mathf.Clamp(entry().Value + d * 5f, -180f, 180f),
        };

        /// <summary>
        /// Steps through an enum setting and wraps, so a row of choices reads the same way as a
        /// number does: left goes back, right goes on, and neither ever dead-ends.
        /// </summary>
        private static T Cycle<T>(T value, int direction) where T : struct, Enum
        {
            var values = (T[])Enum.GetValues(typeof(T));
            var next = (Array.IndexOf(values, value) + direction) % values.Length;
            if (next < 0) next += values.Length;
            return values[next];
        }

        private static Item Axis(string label, Func<BepInEx.Configuration.ConfigEntry<float>> entry) => new()
        {
            Label = label,
            Value = () => $"{entry().Value:F2} m",
            Adjust = d => entry().Value = Mathf.Clamp(entry().Value + d * 0.01f, -0.5f, 0.5f),
        };

        /// <summary>
        /// The settings a reset leaves alone. Not preferences, any of them: the master
        /// switch, the diagnostics, and the four that name something about this machine --
        /// a runtime path, a bone, a range, a latch. Anyone who had to set one of those set
        /// it to make the mod work at all, and a button that quietly undoes that is a button
        /// that takes the headset away.
        ///
        /// Named rather than spelled, so renaming a setting breaks the build here instead of
        /// dropping it out of this list in silence.
        /// </summary>
        private static readonly HashSet<string> NeverReset = new()
        {
            nameof(Plugin.Enabled),
            nameof(Plugin.OpenXrRuntimeJson),
            nameof(Plugin.SubmitDepth),
            nameof(Plugin.LateLatchPose),
            nameof(Plugin.HeadBoneName),
            nameof(Plugin.MeleeRangeName),
            nameof(Plugin.VerboseStartupReport),
            nameof(Plugin.LogCutsceneState),
            nameof(Plugin.LogPoseLatch),
        };

        /// <summary>
        /// Puts every setting back to the value it was born with.
        ///
        /// Read off the plugin's own fields rather than from a list written here. The list
        /// this replaced had drifted: thirty-one settings had been added since it was
        /// written and none of them were in it, so a reset left the dead zones, the panel
        /// and the room-scale settings exactly as they were while saying it had reset them.
        /// A list that has to be extended by hand whenever a setting is added is a list
        /// that will be wrong again by the next release.
        /// </summary>
        private void ResetToDefault()
        {
            var cfg = Plugin.Instance;
            var reset = 0;

            foreach (var field in typeof(Plugin).GetFields(
                         System.Reflection.BindingFlags.Instance
                       | System.Reflection.BindingFlags.Public
                       | System.Reflection.BindingFlags.NonPublic))
            {
                if (NeverReset.Contains(field.Name)) continue;
                if (field.GetValue(cfg) is not BepInEx.Configuration.ConfigEntryBase entry) continue;

                entry.BoxedValue = entry.DefaultValue;
                reset++;
            }
        }

        // -- drawing -------------------------------------------------------------------

        /// <summary>
        /// Draws a header, a window onto the settings, and the key legend.
        ///
        /// Only the rows around the selection are drawn. The list had outgrown the panel, and
        /// the overflow showed as text spilling off the bottom of the background: the background
        /// is stretched to the canvas, so it could only ever be as tall as the canvas had been
        /// told to be, and nothing was telling it. Drawing a window settles both halves at once —
        /// what is drawn always fits, and the panel is then measured from what was drawn, so the
        /// background follows the content by construction instead of being kept in step by hand.
        ///
        /// A window rather than a mask over a taller block, because the page is a single text
        /// object: there is nothing behind a viewport to scroll, and a mask would buy a second
        /// Graphic and a shader dependency to do what choosing the lines already does.
        /// </summary>
        private void Redraw()
        {
            if (_text == null) return;

            var first = Mathf.Clamp(_scroll, 0, Mathf.Max(0, _items.Count - 1));
            var last = Mathf.Min(_items.Count, first + VisibleRows);

            // Held rather than made: this runs every frame the menu is up, and almost every one
            // of those frames throws the whole page away again a few lines below. A builder and
            // its buffer are the part of that which does not have to be rebuilt to find out.
            var sb = _page;
            sb.Clear();

            sb.AppendLine("<b>NobetaVR</b>  <size=18>by Pk_c@ChromaticMod</size>");
            sb.AppendLine(Edge(first > 0, "▲"));

            for (var i = first; i < last; i++)
            {
                var item = _items[i];
                if (item.IsHeading)
                {
                    if (string.IsNullOrEmpty(item.Label)) sb.AppendLine();
                    else sb.Append("<b>").Append(item.Label).AppendLine("</b>");
                    continue;
                }

                var selected = i == _selected;

                // Appended rather than interpolated: the format string builds a whole line of
                // its own before the builder gets it, and there are a dozen of them a frame.
                sb.Append(selected ? "<color=#FFD24A>▸ " : "  ");
                sb.Append(item.Label);
                for (var pad = item.Label.Length; pad < LabelWidth; pad++) sb.Append(' ');
                sb.Append(item.Value?.Invoke());
                if (selected) sb.Append("</color>");
                sb.AppendLine();
            }

            sb.AppendLine(Edge(last < _items.Count, "▼"));
            sb.AppendLine("<size=18>Left stick: move and change   A: activate   Both sticks: close</size>");

            // Redrawn every frame the menu is up, and almost none of those frames change
            // anything. Comparing first keeps the text generation — and the measuring below
            // it — to the frames that actually moved something.
            var page = sb.ToString();
            if (page == _drawn) return;

            _drawn = page;
            _text.text = page;
            FitPanel();
        }

        /// <summary>
        /// The marker for one end of the window: an arrow when the list carries on that way, and
        /// a blank line of the same height when it does not, so the panel does not grow and
        /// shrink by a row as you pass the first and last settings.
        /// </summary>
        private static string Edge(bool more, string arrow) =>
            more ? $"<size=18><color=#7F8C9B>{arrow}</color></size>" : "<size=18> </size>";

        /// <summary>
        /// Sizes the panel to the text that was just drawn.
        ///
        /// The background is stretched to the canvas rather than fitted to the text, so this is
        /// the one thing holding the two together. Only the height is measured: the width is
        /// fixed, because the values change as you adjust them and a panel that breathed a few
        /// pixels wider on every step would read worse than one that is simply wide enough.
        /// </summary>
        private void FitPanel()
        {
            var rect = _root != null ? _root.GetComponent<RectTransform>() : null;
            if (rect == null) return;

            rect.sizeDelta = new Vector2(PanelWidth, _text.preferredHeight + Padding * 2f);
        }

        /// <summary>
        /// Places the panel, driven from the view at the moment the view is final.
        ///
        /// This was called from <c>Update</c>, a whole phase before the head pose is written,
        /// so the menu was placed against a view one frame old and shook against the world
        /// for as long as the head was moving.
        /// </summary>
        internal static void FollowView()
        {
            var self = Instance;
            if (self == null || self._failed || !self._open) return;

            self.Place();
        }

        /// <summary>Sits where the HUD does, so both are read in the same place.</summary>
        private void Place()
        {
            var camera = VrCamera.CameraTransform;
            if (camera == null || _root == null) return;

            // See ViewAnchor for why this is not the flattened forward vector.
            var forward = ViewAnchor.YawForward(camera, _root.transform.forward);

            // Fixed vertically, for the reason the HUD panel is; see HudPanel.Follow.
            var eye = camera.position;
            _root.transform.position = new Vector3(eye.x, VrCamera.SteadyEyeHeight, eye.z)
                                     + forward * Plugin.Instance.MenuDistance.Value;
            _root.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        // -- construction --------------------------------------------------------------

        private bool Build()
        {
            var font = FindFont();
            if (font == null)
            {
                Plugin.Log.LogError("No font could be obtained at all, so the VR menu would be an "
                                  + "empty box. Leaving it off.");
                _failed = true;
                return false;
            }

            _root = new GameObject("NobetaVR Menu");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            // A starting size only: the first draw measures the page and sets the real height.
            var rect = canvas.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(PanelWidth, 760f);

            // One millimetre per canvas unit: the page is authored at a comfortable pixel size
            // and then scaled down to metres, which keeps the text crisp in the headset.
            _root.transform.localScale = Vector3.one * 0.001f;

            var background = new GameObject("Background").AddComponent<Image>();
            background.transform.SetParent(_root.transform, false);
            background.color = new Color(0.04f, 0.04f, 0.06f, 0.85f);
            var backRect = background.GetComponent<RectTransform>();
            backRect.anchorMin = Vector2.zero;
            backRect.anchorMax = Vector2.one;
            backRect.offsetMin = Vector2.zero;
            backRect.offsetMax = Vector2.zero;

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(_root.transform, false);
            _text = textObject.AddComponent<Text>();
            _text.font = font;
            _text.fontSize = 28;
            _text.color = Color.white;
            _text.supportRichText = true;
            _text.alignment = TextAnchor.UpperLeft;
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.verticalOverflow = VerticalWrapMode.Overflow;

            var textRect = _text.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(Padding, Padding);
            textRect.offsetMax = new Vector2(-Padding, -Padding);

            _root.SetActive(false);
            return true;
        }

        /// <summary>
        /// Gets a font without depending on the game having one we can reach.
        ///
        /// The first attempt borrowed TextMeshPro assets from the game and found none:
        /// `Assembly-CSharp` turns out to reference neither TMP nor uGUI text types at all, so
        /// there was nothing to borrow. Asking the operating system removes the question —
        /// `CreateDynamicFontFromOSFont` builds a font from what Windows already has, needs no
        /// asset from anywhere, and cannot be stripped out of the build because it is an engine
        /// binding rather than content.
        /// </summary>
        internal static Font FindFont()
        {
            foreach (var name in new[] { "Segoe UI", "Arial", "Tahoma", "Verdana" })
            {
                try
                {
                    var font = Font.CreateDynamicFontFromOSFont(name, 28);
                    if (font != null) return font;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"OS font '{name}' unavailable: {e.Message}");
                }
            }

            try { return Font.GetDefault(); }
            catch (Exception e) { Plugin.Log.LogWarning($"Font.GetDefault failed: {e.Message}"); }

            return null;
        }
    }
}
