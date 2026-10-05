# SprocketLaserRangefinder

[中文](README.zh.md) | **English**

A prototype BepInEx 6 (IL2CPP) laser ranging and automatic sight-setting mod for
Sprocket. It reads the distance at the sight reticle and the current cannon data,
computes a flat trajectory, and raises the barrel through the game's native cannon
control chain.

## Features

- Uses `SprocketDepth` to get the distance at the centre of the sight reticle.
- Effective ranging range is 20 metres up to the depth texture limit (currently about
  5000 metres).
- Reads the current cannon, shell, muzzle velocity, gravity, and drag curve data from the game.
- Computes the flat-trajectory solution, sight setting angle, and time of flight using
  Sprocket's ballistics model.
- Injects the angle correction into the native `GunLayer` aiming process, preserving the
  game's rotation speed, acceleration, firing arc, damage, and crew efficiency limits.
- Displays the range, sight setting angle, time of flight, and vehicle motion compensation
  state at the top centre of the sight.

## Key bindings

- Left `Ctrl`: range and set the sight automatically.
- `Z`: clear the current range and sight setting.
- `L`: toggle vehicle motion compensation, off by default.

The range and sight setting are kept after you leave the sight and are cleared when the
scene changes. `L` only compensates for the motion of the firing vehicle itself; it does
not track targets and does not compute lead for moving targets.
You can open `MOD KEYBINDINGS` on the Keymapping page in the game settings to change
these three key bindings.

## Installation

1. Install BepInEx 6 (IL2CPP, .NET 6) matching your game version.
2. Place `SprocketLaserRangefinder.dll` into `BepInEx\plugins`.
3. Place [`SprocketModAPI.dll`](https://github.com/furryaxw/SprocketModAPI/releases/latest) `v1.0.0` or later into `BepInEx\plugins`.
4. Place [`SprocketDepth.dll`](https://github.com/furryaxw/SprocketDepth/releases/latest) `v1.0.0` or later into `BepInEx\plugins`.

The current target environment is Sprocket `0.2.55.5`, BepInEx `6.0.0-be.788`
(IL2CPP, .NET 6), and Windows D3D11.

## Building

The project targets .NET 6 and references the local BepInEx core/interop assemblies as
well as `SprocketModAPI.dll` and `SprocketDepth.dll`. The default directory layout is:

```text
G:\Sprocket0.2.55.5\
├── BepInEx\
│   ├── core\
│   ├── interop\
│   └── plugins\SprocketModAPI.dll
│                SprocketDepth.dll
└── mods\SprocketLaserRangefinder\
```

```powershell
dotnet build .\SprocketLaserRangefinder\SprocketLaserRangefinder.csproj `
  --configuration Release
```

By default the build deploys the DLL into `BepInEx\plugins`. Use `-p:SkipModDeploy=true`
to build the DLL only; if the repository lives elsewhere, point at the game root with
`-p:SprocketGameRoot="G:\Sprocket0.2.55.5"`.

## Current limitations

- This version is a Prototype; in-game acceptance has not been completed for all vehicles
  and ranges.
- Does not save or track world coordinates and does not display a predicted point of impact.
- Vehicles without a traverse stabilizer may not be able to apply the traverse correction
  produced by vehicle motion compensation reliably; in that case you can press `L` to turn
  compensation off.

## License

[GPL-3.0-only](LICENSE.txt)
