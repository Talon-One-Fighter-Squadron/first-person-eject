# NO_EjectFirstPerson (BepInEx mod)

Puts your camera **in the pilot’s eyes** while ejecting in **Nuclear Option**.

By default the game follows the ejection seat / pilot in a 3rd-person orbit view. This mod detects when the camera is following your **PilotDismounted** unit and forces a first-person camera at the pilot’s head.

## Install (quick)

1. Install **BepInEx 5** for Nuclear Option (if you don’t already have it).
2. Download this mod to get `NO_EjectFirstPerson.dll`.
3. Extract and copy the whole mod folder to plugins folder path below

```
...\Nuclear Option\BepInEx\plugins\
```

4. Launch the game.

It will try to auto-detect your Steam install and copy the DLL into `BepInEx\plugins`.

## Config

After first run, BepInEx creates:

```
...\Nuclear Option\BepInEx\config\com.nicho.nuclearoption.ejectfirstperson.cfg
```

Useful options:

- `HidePilotRenderers=true` (default): hides your pilot body to reduce clipping.
- `EyeForwardOffset`, `EyeUpOffset`: small offsets to align the view.
- `NearClip`: helps prevent the helmet/body from clipping.
- `LockFovToCurrent`: keep whatever FOV you’re using.

## Notes / limitations

- The mod tries to find a humanoid head bone. If the rig isn’t humanoid, it falls back to searching transform names like “Head / Helmet / Eyes”.
- If the camera is switched away from your ejected pilot, the mod automatically disables and restores renderers / camera settings.