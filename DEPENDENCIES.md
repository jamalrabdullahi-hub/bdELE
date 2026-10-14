# BD Armory: Enhanced Lethality Edition (ELE): dependencies and recommended mods

ELE is an add-on for **BDArmory**, not a replacement: it needs BDArmory installed to run.
Built and tested on **KSP 1.12.5** with the stock **BDArmory 1.13.1.0 release** DLL (ELE never modifies BDArmory's files; it hooks in with Harmony at runtime).

## Required

| Mod | Why | Get it |
|---|---|---|
| BDArmory (Continued) 1.13.x | ELE extends it (weapons, AI, radar, teams) | https://github.com/BrettRyland/BDArmory/releases · CKAN `BDArmoryPlus` |
| Physics Range Extender | BDArmory needs it | https://github.com/jrodrigv/PhysicsRangeExtender/releases |
| Harmony (HarmonyKSP, `000_Harmony`) | ELE's runtime hooks | https://github.com/KSPModdingLibs/HarmonyKSP/releases |
| Module Manager | ELE's part patches | https://github.com/sarbian/ModuleManager |

## Optional: switch on ELE features when present

| Mod | ELE feature it enables |
|---|---|
| HullCam VDS Continued | EO/IR camera views, camera HUDs, loitering-munition / FPV camera handover, munition-target camera |
| Infernal Robotics Next (MagicSmokeIndustries) | IR turrets: aiming servos, gantry / deploy animations, VTOL tilt rotors |
| Kerbal Konstructs + KerbinSide Remastered | Factions & bases campaign, airfields, base SAM sites |
| OfCourseIStillLoveYou | Camera feed windows |
| RasterPropMonitor + ASET Props (ALCOR MFD) | ELE MFD pages; ELE pages swapped into every RPM screen (Vessel Viewer → radar, resources → weapons, ...) |
| FreeIva | Walkable ELE cockpits |
| Probe Control Room | ELE drone operations room |
| KAS / KIS | KAS operations (refuel / tow helpers) |
| AARS (Automated Aerial Refuelling System) | Air-to-air refuelling ops |
| MouseAimFlight | Recommended with the ELE flight control system |
| Waterfall + Stock Waterfall Effects | Engine plumes (ELE adds fallback plumes to engines without configs) |
| Large Boat Parts Pack 3.9.2 (Modern + Required + SM_Industries) | Dynamic Hull "ship lines" (corvette / destroyer / carrier / trimaran / sub) |
| Ferram Aerospace Research (+ ModularFlightIntegrator, KSPBurst) | FAR aerodynamics charts in the VAB/SPH |
| Destruction Effects | Burning wreck flames used by ELE's lingering fires |
| SCANsat | Optional map data for C2 |
| Toolbar Controller + Click Through Blocker | Toolbar handling used by several of the mods above |

Included inside ELE (no separate install): **Volumetric Explosion FX 1.0** by Warnix (MIT), see `GameData/BDArmoryAvionics/VEFX/LICENSE`.

## Recommended

| Mod | Why |
|---|---|
| Kerbal Joint Reinforcement (Next) | Large craft and ships hold together (strongly recommended with LBP) |
| Quantum Struts Continued | Struts without physical parts |
| KSPCommunityFixes | Stability and loading fixes |
| TweakScale | Rescaling parts |
| B9 Procedural Wings Modified | Fighter wings |
| Procedural Parts | Fuselage / tank shapes |
| Airplane Plus, Mk2 Stockalike Expansion, QuizTech Aero Pack Continued | Aircraft parts |
| Kerbal Foundries (+ KSPWheel, TexturesUnlimited) | Tracks and wheels for ground vehicles |
| Conformal Decals (+ B9PartSwitch, Shabby) | Markings and decals |
| VesselMover | Placing ships and vehicles |
| Kerbal Engineer Redux | Build stats |
| Scatterer | Looks |

## Building ELE from source

1. Copy from your KSP install `KSP_x64_Data/Managed/*.dll` into `_LocalDev/KSPRefs/` (next to the `BDArmory` folder of this repo; not uploaded: they are Squad's).
2. Copy `BDArmory.dll` and `0Harmony.dll` (from a BDArmory release / HarmonyKSP) into `_LocalDev/BDArmoryRef/`.
3. `dotnet build BDArmoryAvionics -c Release` → `BDArmoryAvionics/bin/Release/BDArmoryAvionics.dll`.
4. Install: copy `BDArmoryAvionics/GameData/BDArmoryAvionics` into KSP's `GameData` (the built DLL goes in its `Plugins`). Close KSP first.

Unity helper projects (parts / shaders) are in `Tools/`: open with Unity 2019.4.18f1. The part exporter needs KSP PartTools (from Squad, not included).
