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
- **The interface readable in VR**, locked in front of you, hideable, with a reticle in
  the world that marks where the shot will actually land

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
.\deploy.ps1 -Uninstall  # remove every file the mod added
```

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

Touch-style controllers. Everything here is remappable from
`BepInEx/config/fr.chromatic.nobetavr.cfg`.

| Input | Action |
| --- | --- |
| Left stick | Move — forward, back and strafe, relative to where you are looking |
| Right stick | Turn — snap by default, 45° a step; smooth is a setting |
| Left stick click | Run |
| Right stick click, held | The magic wheel; point at an arcane with the same stick and let go |
| A | Jump |
| B | Dodge roll |
| X | Use the selected item |
| Y | Interact |
| Y, held two seconds | The game's pause menu |
| Left trigger | Pray, to take her mana back |
| Right trigger | Shoot |
| Left grip | Step through the items |
| Right grip | Focus — the game's held shot |
| Both grips together | Recentre: puts your head back on Nobeta |
| Both sticks clicked | The mod's VR settings |

Three of those share a control. Y is interact when tapped and the pause menu when held, so
interact happens when you let go rather than when you press. The grips recentre only when
squeezed together — squeeze the second one later and it means what it says on its own, so
you can reach for an item without losing your focus. And the right stick stops turning while
the wheel is up, since that is the stick pointing around it.

Walking physically moves Nobeta, through the game's own collision. She turns to face wherever
you look, including when you turn on the spot.

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
