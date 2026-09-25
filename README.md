# NobetaVR

<img width="1658" height="949" alt="75f78761-5173-4657-ab86-306161a69016" src="https://github.com/user-attachments/assets/133ece3b-20d3-4260-94e5-b9c16f6b5b9c" />

A VR mod for **[Little Witch Nobeta](https://store.steampowered.com/app/1049890/)**
(Pupuya Games).

LWN is a third person game, this mod turn it into a first person native like VR Experience.
With motion control, confort option and game adjustments.

It is strongly advised to look at the **Controls** section of this page before you start playing !
This game have some very specific actions that are difficult to guess.

If you like my work you can follow me on Patreon ( free membership ), I try to make like native mode for beautiful games!

https://patreon.com/ChromaticMod

<a href="https://patreon.com/ChromaticMod">
  <img width="200" height="105" alt="imakevrmodforgames-preview" src="https://github.com/user-attachments/assets/0517352b-e120-47bc-b062-b85fc333f814" />
</a>

OR

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/A0Y524C5N8)

---

Check the presentation video : https://www.youtube.com/watch?v=jdqq6_6R2Xo

---

## What it does

- **First-person 6DOF view** anchored to the character skeleton's `head` bone,  direction of movement is based on headset, controls are adapted for First person VR
- **Room scale movement with collisions** you can move physically the character will follow, collision will prevent your camera to go inside walls
- **Melee combat** hit detection on the staff
- **Touch controller bindings** for the whole control set, remappable
- **Motion-controlled hands** control your staff & melee attack with your hands, handed back to the game for cutscenes and menus
- **WristBand hud** health, stamina and mana worn on your left wrist instead of framed across the top of the view;
- **Adapted UI** ui hide and show when you need them, and don't get in the way, fade,fx are adpated for VR

## Installing

Unzip the release archive into the game folder, next to `LittleWitchNobeta.exe`, and launch
the game normally. That is all

```
...\Steam\steamapps\common\Little Witch Nobeta\
```

### Uninstalling

Run NobetaVR-Uninstall.bat it will remove all the file linked to the VR mod along with your config

## Controls

| Input | Action |
| --- | --- |
| Left stick | Move — forward, back and strafe, relative to where you are looking |
| Right stick | Turn — snap by default, 55° a step; smooth is a setting |
| Left stick click | Run |
| Right stick hold | The spell wheel; push the same stick towards an arcane and let go |
| A | Jump; in a conversation, the next line |
| B | Dodge |
| X | Use the selected item |
| Y | Interact |
| Y, held two seconds | The game's pause menu |
| Left trigger | Pray, charge for spell ult |
| Right trigger | Shoot |
| Left grip | Step through the items — a tap is enough |
| Right grip | Focus — the game's held shot |
| Both grips together | Recentre: if your body feels off -> use the headset recenter function FIRST and then press both grip |
| Both sticks clicked | The mod's VR settings |
| Swinging the wand | Melee |

**Special actions :**

-You can recover while mid-air or while grounded by pressing B (Dodge) with the right timing.

-You can trigger parry by swinging your wand at the right moment, just before recieving an attack ( the timing is short ! )


**Statue Menu**

| Input | Action |
| --- | --- |
| Left grip - Right grip | Spend your soul essence into skills / Trade |
| Left trigger -  Right trigger | Switch costumes |

## Known Issues

-If your body position is not centered -> use the headset recenter function FIRST and then press both grip, it should fix every edge cases
-Sometimes the camera may not have the perfect orientation when a cutscene begin, so you may have turn your head a bit, you can also turn physically the character will follow your orientation when you get back control.
-If the application doesn't have focus you may not be able to confirm choice in menu.

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

## Licence

Code in this repository: MIT, see [LICENSE](LICENSE).
Third-party components are listed in [THIRD-PARTY.txt](THIRD-PARTY.txt) with their own
licences.
