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
            Place();
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

            if (both && !_toggleHeld) Toggle();
            _toggleHeld = both;
        }

        private void Toggle()
        {
            if (!_open && _root == null && !Build()) return;

            _open = !_open;
            if (_root != null) _root.SetActive(_open);
            if (_open) Redraw();
            Plugin.Log.LogInfo(_open ? "VR menu opened" : "VR menu closed");
        }

        // -- input ---------------------------------------------------------------------

        private void HandleNavigation(VrControls controls)
        {
            var stick = controls.Input.LeftStick;
            const float dead = 0.5f;

            var vertical = Mathf.Abs(stick.y) > Mathf.Abs(stick.x);

            if (vertical && Mathf.Abs(stick.y) > dead)
            {
                if (Repeat(stick)) MoveSelection(stick.y > 0 ? -1 : 1);
            }
            else if (!vertical && Mathf.Abs(stick.x) > dead)
            {
                if (Repeat(stick)) _items[_selected].Adjust?.Invoke(stick.x > 0 ? 1 : -1);
            }
            else
            {
                _lastStick = Vector2.zero;
            }

            // A activates; the same button the game uses to confirm.
            if (controls.Input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary) && !_activateHeld)
                _items[_selected].Activate?.Invoke();
            _activateHeld = controls.Input.Pressed(VrInput.Hand.Right, VrInput.Button.Primary);
        }

        private bool _activateHeld;

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
                Label = "Smooth turn speed",
                Value = () => $"{cfg.SmoothTurnSpeed.Value:F0}°/s",
                Adjust = d => cfg.SmoothTurnSpeed.Value = Mathf.Clamp(cfg.SmoothTurnSpeed.Value + d * 10f, 20f, 360f),
            });
            _items.Add(new Item
            {
                Label = "Dodge",
                Value = () => cfg.DodgeAlwaysBackstep.Value ? "Always hop" : "Game's choice",
                Adjust = _ => cfg.DodgeAlwaysBackstep.Value = !cfg.DodgeAlwaysBackstep.Value,
            });

            _items.Add(new Item { Label = "", IsHeading = true });
            _items.Add(new Item { Label = "HEAD", IsHeading = true });

            _items.Add(Axis("Head offset X", () => cfg.HeadOffsetX));
            _items.Add(Axis("Head offset Y", () => cfg.HeadOffsetY));
            _items.Add(Axis("Head offset Z", () => cfg.HeadOffsetZ));

            _items.Add(new Item
            {
                Label = "Head bobbing",
                Value = () => cfg.HeadBobbing.Value ? "On" : "Off",
                Adjust = _ => cfg.HeadBobbing.Value = !cfg.HeadBobbing.Value,
            });
            _items.Add(new Item
            {
                Label = "Head hide distance",
                Value = () => $"{cfg.HeadHideDistance.Value:F2} m",
                Adjust = d => cfg.HeadHideDistance.Value =
                    Mathf.Clamp(cfg.HeadHideDistance.Value + d * 0.01f, 0f, 1f),
            });

            _items.Add(new Item { Label = "", IsHeading = true });
            _items.Add(new Item { Label = "COMFORT", IsHeading = true });

            _items.Add(new Item
            {
                Label = "View on death",
                Value = () => cfg.ThirdPersonOnDeath.Value ? "Third person" : "Stay in her head",
                Adjust = _ => cfg.ThirdPersonOnDeath.Value = !cfg.ThirdPersonOnDeath.Value,
            });
            _items.Add(new Item
            {
                Label = "Cutscene frame",
                Value = () => cfg.CutsceneVignette.Value ? "On" : "Off",
                Adjust = _ => cfg.CutsceneVignette.Value = !cfg.CutsceneVignette.Value,
            });
            _items.Add(new Item
            {
                Label = "Frame width",
                Value = () => $"{cfg.CutsceneVignetteWidth.Value:F0}°",
                Adjust = d => cfg.CutsceneVignetteWidth.Value =
                    Mathf.Clamp(cfg.CutsceneVignetteWidth.Value + d * 2f, 10f, 136f),
            });
            _items.Add(new Item
            {
                Label = "Frame height",
                Value = () => $"{cfg.CutsceneVignetteHeight.Value:F0}°",
                Adjust = d => cfg.CutsceneVignetteHeight.Value =
                    Mathf.Clamp(cfg.CutsceneVignetteHeight.Value + d * 2f, 10f, 136f),
            });
            _items.Add(new Item
            {
                Label = "Frame edge",
                Value = () => cfg.CutsceneVignetteSoftness.Value <= 0.001f
                    ? "Hard"
                    : $"{cfg.CutsceneVignetteSoftness.Value:F2}",
                Adjust = d => cfg.CutsceneVignetteSoftness.Value =
                    Mathf.Clamp(cfg.CutsceneVignetteSoftness.Value + d * 0.02f, 0f, 1f),
            });

            _items.Add(new Item { Label = "", IsHeading = true });
            _items.Add(new Item { Label = "HANDS", IsHeading = true });

            _items.Add(new Item
            {
                Label = "Hand steadiness",
                Value = () => cfg.HandSteadiness.Value <= 0.001f
                    ? "Off"
                    : $"{cfg.HandSteadiness.Value:F2}",
                Adjust = d => cfg.HandSteadiness.Value =
                    Mathf.Clamp(cfg.HandSteadiness.Value + d * 0.05f, 0f, 1f),
            });
            _items.Add(Degrees("Hand pitch", () => cfg.HandRotationPitch));
            _items.Add(Degrees("Hand yaw", () => cfg.HandRotationYaw));
            _items.Add(Degrees("Hand roll", () => cfg.HandRotationRoll));
            _items.Add(new Item
            {
                Label = "Wand steadied",
                Value = () => cfg.HoldWandStill.Value ? "On" : "Off",
                Adjust = _ => cfg.HoldWandStill.Value = !cfg.HoldWandStill.Value,
            });
            _items.Add(new Item
            {
                Label = "Wand follow speed",
                Value = () => $"{cfg.WandFollowSpeed.Value:F1}/s",
                Adjust = d => cfg.WandFollowSpeed.Value =
                    Mathf.Clamp(cfg.WandFollowSpeed.Value + d * 0.5f, 0.5f, 30f),
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
            _items.Add(new Item
            {
                Label = "Reticle",
                Value = () => cfg.ShowAimReticle.Value ? "On" : "Off",
                Adjust = _ => cfg.ShowAimReticle.Value = !cfg.ShowAimReticle.Value,
            });
            _items.Add(new Item
            {
                Label = "Reticle size",
                Value = () => $"{cfg.AimReticleSize.Value * 100f:F1}",
                Adjust = d => cfg.AimReticleSize.Value =
                    Mathf.Clamp(cfg.AimReticleSize.Value + d * 0.002f, 0.004f, 0.06f),
            });
            _items.Add(new Item
            {
                Label = "Game crosshair",
                Value = () => cfg.HideGameCrosshair.Value ? "Hidden" : "Shown",
                Adjust = _ => cfg.HideGameCrosshair.Value = !cfg.HideGameCrosshair.Value,
            });

            _items.Add(new Item { Label = "", IsHeading = true });
            _items.Add(new Item { Label = "MELEE", IsHeading = true });

            _items.Add(new Item
            {
                Label = "Swing to hit",
                Value = () => cfg.Melee.Value ? "On" : "Off",
                Adjust = _ => cfg.Melee.Value = !cfg.Melee.Value,
            });
            _items.Add(new Item
            {
                Label = "Swing speed",
                Value = () => $"{cfg.MeleeSpeed.Value:F2} m/s",
                Adjust = d => cfg.MeleeSpeed.Value =
                    Mathf.Clamp(cfg.MeleeSpeed.Value + d * 0.1f, 0.2f, 6f),
            });
            _items.Add(new Item
            {
                Label = "Swing distance",
                Value = () => $"{cfg.MeleeDistance.Value:F2} m",
                Adjust = d => cfg.MeleeDistance.Value =
                    Mathf.Clamp(cfg.MeleeDistance.Value + d * 0.02f, 0.05f, 1f),
            });
            _items.Add(new Item
            {
                Label = "Swing release",
                Value = () => $"{cfg.MeleeReleaseSpeed.Value:F2} m/s",
                Adjust = d => cfg.MeleeReleaseSpeed.Value =
                    Mathf.Clamp(cfg.MeleeReleaseSpeed.Value + d * 0.1f, 0.05f, 4f),
            });
            _items.Add(new Item
            {
                Label = "Ground swing",
                Value = () => cfg.MeleeFreeSwingOnGround.Value ? "Free" : "Animated",
                Adjust = _ => cfg.MeleeFreeSwingOnGround.Value = !cfg.MeleeFreeSwingOnGround.Value,
            });
            _items.Add(new Item
            {
                Label = "Hitbox size",
                Value = () => $"×{cfg.MeleeHitboxSize.Value:F1}",
                Adjust = d => cfg.MeleeHitboxSize.Value =
                    Mathf.Clamp(cfg.MeleeHitboxSize.Value + d * 0.25f, 0.5f, 8f),
            });
            _items.Add(new Item
            {
                Label = "Swing voice",
                Value = () => cfg.MeleeSwingVoice.Value ? "On" : "Off",
                Adjust = _ => cfg.MeleeSwingVoice.Value = !cfg.MeleeSwingVoice.Value,
            });
            _items.Add(new Item
            {
                Label = "Hitbox reach",
                Value = () => $"{cfg.MeleeHitboxReach.Value:F2} m",
                Adjust = d => cfg.MeleeHitboxReach.Value =
                    Mathf.Clamp(cfg.MeleeHitboxReach.Value + d * 0.02f, 0f, 2f),
            });
            _items.Add(new Item
            {
                Label = "Show hitbox",
                Value = () => cfg.MeleeShowHitbox.Value ? "On" : "Off",
                Adjust = _ => cfg.MeleeShowHitbox.Value = !cfg.MeleeShowHitbox.Value,
            });

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
            _items.Add(new Item
            {
                Label = "Reset to default",
                Value = () => "(A)",
                Activate = ResetToDefault,
            });

            MoveSelection(1);   // land on the first real item rather than a heading
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

        private void ResetToDefault()
        {
            var cfg = Plugin.Instance;
            foreach (var entry in new BepInEx.Configuration.ConfigEntryBase[]
                     {
                         cfg.SmoothTurn, cfg.SnapTurnDegrees, cfg.SmoothTurnSpeed,
                         cfg.HeadOffsetX, cfg.HeadOffsetY, cfg.HeadOffsetZ,
                         cfg.HeadBobbing, cfg.HeadHideDistance,
                         cfg.CutsceneVignette, cfg.CutsceneVignetteWidth,
                         cfg.CutsceneVignetteHeight, cfg.CutsceneVignetteSoftness,
                         cfg.CutsceneVignetteFade, cfg.CutsceneVignetteDistance,
                         cfg.HandSteadiness,
                         cfg.HandRotationPitch, cfg.HandRotationYaw, cfg.HandRotationRoll,
                         cfg.HoldWandStill, cfg.WandFollowSpeed,
                         cfg.AimFromHand,
                         cfg.AimPitchOffset, cfg.AimYawOffset, cfg.AimRollOffset,
                         cfg.ShowAimReticle, cfg.AimReticleSize, cfg.HideGameCrosshair,
                         cfg.Melee, cfg.MeleeSpeed, cfg.MeleeDistance,
                         cfg.MeleeReleaseSpeed, cfg.MeleeCooldown,
                         cfg.MeleeHitboxReach, cfg.MeleeShowHitbox,
                         cfg.MeleeFreeSwingOnGround,
                         cfg.MeleeHitboxSize, cfg.MeleeTrailSeconds, cfg.MeleeSwingVoice,
                         cfg.Haptics, cfg.HapticsHand, cfg.HapticsStrength,
                         cfg.HapticsMinAmplitude, cfg.HapticsMaxSeconds, cfg.HapticsFrequency,
                         cfg.DodgeAlwaysBackstep, cfg.ThirdPersonOnDeath,
                     })
            {
                entry.BoxedValue = entry.DefaultValue;
            }
            Plugin.Log.LogInfo("VR menu reset to defaults");
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

            var sb = new StringBuilder();
            sb.AppendLine("<b>NobetaVR</b>  <size=18>by Pk_c@ChromaticMod</size>");
            sb.AppendLine(Edge(first > 0, "▲"));

            for (var i = first; i < last; i++)
            {
                var item = _items[i];
                if (item.IsHeading)
                {
                    sb.AppendLine(string.IsNullOrEmpty(item.Label) ? "" : $"<b>{item.Label}</b>");
                    continue;
                }

                var marker = i == _selected ? "<color=#FFD24A>▸ " : "  ";
                var close = i == _selected ? "</color>" : "";
                sb.AppendLine($"{marker}{item.Label,-22}{item.Value?.Invoke()}{close}");
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

        /// <summary>Sits where the HUD does, so both are read in the same place.</summary>
        private void Place()
        {
            var camera = VrCamera.CameraTransform;
            if (camera == null || _root == null) return;

            var forward = camera.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return;
            forward.Normalize();

            _root.transform.position = camera.position + forward * Plugin.Instance.MenuDistance.Value;
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
            Plugin.Log.LogInfo($"VR menu built with font '{font.name}'");
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
        private static Font FindFont()
        {
            foreach (var name in new[] { "Segoe UI", "Arial", "Tahoma", "Verdana" })
            {
                try
                {
                    var font = Font.CreateDynamicFontFromOSFont(name, 28);
                    if (font != null)
                    {
                        Plugin.Log.LogInfo($"VR menu using the OS font '{name}'");
                        return font;
                    }
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
