# NobetaVR

A VR mod for **[Little Witch Nobeta](https://store.steampowered.com/app/1049890/)**
(Pupuya Games).

> **Status: in development — not yet playable.** There is no release to download yet.
>
> The engine work is done and verified in game. BepInEx 6 runs on the shipping IL2CPP
> build, Unity's OpenXR provider is injected into a game that shipped without XR, and the
> XR display subsystem runs: `XRSettings.enabled=True`, device `OpenXR Display`, multi-pass.
> The gameplay layer — camera, controls, room-scale, UI, hands — is what comes next.

Not affiliated with Pupuya Games or Justdan International. No game assets and no game code
are redistributed.

## What it will do

- **Stereo VR camera at Nobeta's eyes**, with the game's own camera logic left running, so
  cutscenes keep their framing instead of being replaced by a fixed view
- **Room-scale movement** — your physical position and rotation move the character, and the
  game's own collision resolves it
- **Touch controller bindings** for the whole control set, remappable
- **Motion-controlled hands**, the character's own hands on your controllers, shown while
  she is yours to move and handed back to the game for cutscenes and menus
- **A VR settings page** that applies live and is adjusted from inside the headset
- **The interface readable in VR**, locked in front of you and drawn over the world rather
  than behind it, with a reticle in the world that marks where the shot will actually land
- **An interface thinned to what a headset wants** — health, stamina and mana worn on your
  left wrist instead of framed across the top of the view; the spell charge bar gone, since
  the wand already tells you; the soul count and the item bar fading in only when they have
  something to say. All of it reversible from the VR settings page, live
- **The game's own rumble, in your hands** — the haptics were always there, they were just
  going to a gamepad nobody is holding; the same events at the same strengths now arrive in
  the controllers, and the game's vibration setting still turns them off
- **Comfort measures for the moments the game takes the camera** — cutscenes step you back
  out of her head to the game's own camera, so a sweep is a camera moving through a room
  rather than your own head being turned for you, and the scene is framed as it was authored;
  each shot is turned to where you are already looking, at the start and again at every cut,
  so it plays in front of you rather than over your shoulder; the camera's breathing sway and
  combat shake are switched off; dying does the same and returns when she is yours again; and
  the dodge is always the backward hop, because the roll it would otherwise pick takes the
  camera over with her
- **Her scenes are hers** — while the game is framing one, the controls stand down: the
  trigger does not fire her magic, the stick does not walk her off the mark the scene put her
  on, the right stick does not turn a camera the scene is authoring, and her head is not
  dragged around by where you happen to be looking. Everything comes back the moment she does
- **Fades that actually fade** — the game hides its transitions behind a full-screen black
  image, which in a headset is a black rectangle hanging in front of you with the level still
  visible around it. The game's own timing drives a real fade of the whole view instead

## Installing

Unzip the release archive into the game folder, next to `LittleWitchNobeta.exe`, and launch
the game normally. That is all — no injector, no configuration step.

```
...\Steam\steamapps\common\Little Witch Nobeta\
```

## Building

Requires the .NET SDK and an install of the game.

```powershell
.\deploy.ps1 -Loader     # once per game install: unpacks BepInEx into the game folder
.\deploy.ps1             # build the plugin and install it
.\deploy.ps1 -Package    # build dist\NobetaVR-<version>.zip, the archive above
.\deploy.ps1 -Uninstall  # remove every file the mod added
```

The game folder is found through Steam itself, libraries on other drives included. Pass
`-GameDir` for a copy Steam has never heard of.

The first launch after `-Loader` generates `BepInEx\interop` from the game's own binary —
the proxy assemblies the plugin is compiled against. It takes about fifteen seconds and
needs a network connection once, then never again. Players see the same one-off wait: those
assemblies are derived from the game and are not something this project redistributes.

The source carries its own reasoning. Where something looks arbitrary it is usually
load-bearing, and the comment next to it says what was measured and why the obvious
alternative does not work — the OpenXR loader sequence, the camera ownership dance with
`PlayerCamera`, and the several places IL2CPP stripping forced a different route are all
explained where they live.

## Controls

Touch-style controllers. The buttons themselves are fixed; how they behave is tunable from
`BepInEx/config/fr.chromatic.nobetavr.cfg` — the dead zones, the snap angle, the grip
threshold, how fast the spell wheel moves, and which way the left grip steps through the
items.

| Input | Action |
| --- | --- |
| Left stick | Move — forward, back and strafe, relative to where you are looking |
| Right stick | Turn — snap by default, 55° a step; smooth is a setting |
| Left stick click | Run |
| Right stick click | The spell wheel; push the same stick towards an arcane and let go |
| A | Jump; in a conversation, the next line |
| B | Dodge — the backward hop, never the roll; in a conversation, the skip menu |
| X | Use the selected item |
| Y | Interact |
| Y, held two seconds | The game's pause menu |
| Left trigger | Pray, to take her mana back |
| Right trigger | Shoot |
| Left grip | Step through the items — a tap is enough |
| Right grip | Focus — the game's held shot |
| Both grips together | Recentre: puts your head back on Nobeta, straight ahead on a menu, or the shot back in front of you during a cutscene |
| Both sticks clicked | The mod's VR settings |
| Swinging the wand | Melee — no button; see below |

Three of those share a control. Y is interact when tapped and the pause menu when held, so
interact happens when you let go rather than when you press. The grips recentre only when
squeezed together — squeeze the second one later and it means what it says on its own, so
you can reach for an item without losing your focus. And the right stick stops turning while
the wheel is up, since that is the stick pointing around it.

The wheel is the third and the awkward one, because a thumb pressing a stick down cannot then
push it sideways. So the click is read like Y: hold it and the wheel behaves as the game's own
does, closing when you let go; tap it — including the tap a hold becomes the moment your thumb
rolls off — and the wheel stays up with the stick free. Either way you finish the same, by
pushing towards a spell and letting the stick come home. A tap with nothing chosen closes on a
second tap.

The wheel is also pushed at harder than your thumb pushes. It eases towards what it is handed
rather than following it, at a fixed rate of its own, which is polish on a monitor and lag in a
headset where the whole interaction is one push and a release — so `SpellWheelSpeed` scales the
vector it is given, which puts the target further out and gets the arrow most of the way there
sooner. The angle is never touched: the wheel is always handed the direction your thumb is
holding. Above one it also brings the wheel up at full opacity instead of fading it in. Three by
default.

The title screen recentres itself, half a second after it comes up and behind the fade. It is
the one screen with nothing to point you at — no Nobeta to face, no camera whose yaw is the
answer — so which way you face there is whichever way you were standing when the runtime fixed
its tracking origin, or wherever you had turned to in the stage you just left.
`RecentreOnTitle` in the config turns it off.

The grips are read off their own axis rather than from the runtime's grip button, which on
Touch only registers when the squeeze is most of the way in. `GripThreshold` in the config
is where that threshold lives if a light squeeze is still not light enough, or if a resting
finger is changing your item.

In the game's own menus the buttons mean what they mean on a pad: the left stick moves,
**A** confirms, **B** goes back, and the triggers page left and right. Two more are worth
knowing, because neither has an equivalent on the gameplay side:

| Input | In a menu |
| --- | --- |
| Left trigger | Previous page |
| Right trigger | Next page |
| Right grip, **held** | Spend souls — levelling up at a statue, and trading. The game counts them out for as long as you hold it |
| Left grip | The special action a screen offers, where one does |

The page turn is on the triggers because it is the one thing you do repeatedly while reading a
menu, and the grips take what the triggers were doing: spending souls is a hold, and a squeeze
is a better shape for a hold than a trigger kept pulled. The mod's own settings panel pages the
same way, by section.

Walking physically moves Nobeta, through the game's own collision. She turns to face wherever
you look, including when you turn on the spot.

Melee has no button. Swinging your right hand fast enough and far enough — 3 m/s held for
15 cm, both settings — swings the wand. The thresholds are measured relative to your own head,
so walking and turning do not count as swings.

What that swing does depends on your feet. **In the air** it is the game's own attack,
animation and all, because attacking in mid-air is also how you hang there and that hang is
only available through the game's own call. **On the ground** the animation would plant your
feet and swing the wand for you, so it is skipped: the hitbox opens on its own and the swing
sound, the voice, the impact effect and the damage are all still the game's.

The hitbox is a capsule lying on the wand either way, on the same line the shot goes down.
`MeleeHitboxReach` is where its middle sits along the wand, `MeleeHitboxLength` is how far it
runs along it and `MeleeHitboxRadius` is how thick it is, all in metres, with
`MeleeHitboxPitch` and `MeleeHitboxYaw` to tilt it off that line — that volume is the
whole geometry of a blow, so those three settings are the whole of how forgiving melee feels.
`MeleeShowHitbox` draws it while you tune them, orange for as long as it is open.

The tilt turns the capsule about its own base, so the near end stays where the reach put it and
only the far end swings — it is for laying the capsule along the sceptre you can see, when
the angle the model sits at in her hand is not the angle the controller points at. It moves the
hitbox alone: the shot still goes down the wand line untouched.

The game's own melee has no capsule to ask for: its hitbox is one point with one radius. The
capsule is served by moving that point to whatever part of the axis is nearest what you swung
at, so the game's own collision — its damage, its knockback, its hit effects — answers a
question a capsule collider would have answered. One target per test, ranked by the game's own
tags: enemies first, then breakables, then everything else.

`MeleeRequireWand` keeps melee to the hand that is holding something. The game puts the wand
away when her animations have no use for it, and a hitbox that stays live through that is a
blow struck with an empty hand; with this on, the ranges go back to the character until the
wand is out again.

The swing trail is not drawn at all. It is an `XWeaponTrail`, which does not follow an object:
it samples two transforms every frame and draws a ribbon between where they were and where they
are, and those two live on an arm this mod has collapsed into the shoulder — so the ribbon was
drawn correctly along a line with no wand on it. Moving it onto the wand was tried and
abandoned: the two points were replaced with two of the mod's own on the known wand line, then
made settable when that came out wrong, and the instrumentation showed all of it working — the
game never took the points back, all four ribbons lit up on them, the segment ran from the hand
out along the wand — with the ribbon still not where it belonged. So it is switched off, at
`WizardGirlManage.OpenWTrail` and `PlayerEffectPlay.SetWTrailActive`, which are the two calls
that raise it deliberately. Both are hers by type, so no enemy loses the weapon trail that
tells you it is swinging.

That alone did not hold it. A costume change destroys her effect objects and instantiates new
ones, and a fresh `XWeaponTrail` comes up through its own `OnEnable` without any of her calls
being made — so her four are also re-read whenever the set underneath changes, and the
component is disabled outright, with its mesh object switched off behind it.

## When the headset stays black

`BepInEx/LogOutput.log` is where the mod says what happened, and on any XR failure it dumps
the OpenXR provider's own diagnostic report — runtime name, extension list, and the actual
`XrResult` — with a plain sentence underneath saying what it means.

| What the log says | What it means |
| --- | --- |
| `xrCreateInstance: XR_ERROR_RUNTIME_FAILURE` | The runtime is installed and answering but has no session to give. Put the headset on, get it to its own home environment, then launch. |
| `XR_ERROR_FORM_FACTOR_UNAVAILABLE` | The runtime is running and reports no headset connected. |
| `No 'OpenXR Display' descriptor` | The provider files are missing or arrived after launch. The manifest is read during engine start-up, so it must be in place before the game starts. |
| `XR_ERROR_FUNCTION_UNSUPPORTED` | A fault in the mod's own start-up sequence, not in your runtime. |

If several OpenXR runtimes are installed, the game may come up on one you did not intend —
Virtual Desktop and the Oculus app both tend to claim the system-wide setting. Set
`OpenXrRuntimeJson` in `BepInEx/config/fr.chromatic.nobetavr.cfg` to the manifest you want.
It applies to this game only; the machine-wide setting is never modified.

## Licence

Code in this repository: MIT, see [LICENSE](LICENSE).
Third-party components are listed in [THIRD-PARTY.txt](THIRD-PARTY.txt) with their own
licences.
