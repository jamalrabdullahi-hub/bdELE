# BD Armory: Enhanced Lethality Edition (ELE) 1.0.0

A large add-on for **BDArmory** in **KSP 1.12.x**: smarter radar and electronic warfare, C2 and IADS, a PvE campaign with an AI commander, AI flight control, explosions and damage, helicopters, ground vehicles, procedural engines, cockpit screens and drone operations.

BDArmory itself is **not modified**. ELE is a separate mod (`GameData/BDArmoryAvionics`) that hooks into BDArmory at runtime.

## Requirements

| Mod | Notes |
|---|---|
| KSP 1.12.x | Built and tested on 1.12.5 |
| BDArmory Continued 1.13.x | Built against 1.13.1.0 |
| HarmonyKSP | `GameData/000_Harmony` |
| Module Manager | 4.2.x |

## Optional mods: ELE detects these and uses them if present

| Mod | What ELE adds |
|---|---|
| HullCam VDS Continued | EO/IR thermal cameras, FCS HUDs, camera feeds on the screens |
| RasterPropMonitor + ASET Props | ELE pages on ASET ALCOR MFDs (CAM, WPN, ENG, RDR, SYS, C2, AP) |
| FreeIva | Walk around the gunship cockpit, exit through the canopy hatch |
| Probe Control Room | One-operator drone operations room for probe-cored drones |
| Infernal Robotics Next | IR turrets, gantries, VTOL tilt servos |
| KAS | Helicopter hoist and sling loads (CSAR) |
| Destruction Effects | Persistent fires |
| Kerbal Konstructs (+ a base pack) | Factions and bases campaign on real bases |
| Through The Eyes of a Kerbal | First-person play; works with the cockpit screens |
| Procedural Parts, B9 Procedural Wings, AARS | Used by the example craft only |

Without any of them, ELE still works. Only the matching features are missing.

## Install

1. Install the requirements.
2. Copy the `GameData/BDArmoryAvionics` folder from this zip into your KSP `GameData` folder.
3. *Optional:* copy `Ships/SPH/*.craft` into `KSP/Ships/SPH` (stock craft list) or your save's `Ships/SPH`.

The result should be `KSP/GameData/BDArmoryAvionics/Plugins/BDArmoryAvionics.dll`.

## Highlights

- **Sensors and EW:** threaded sensor engine with terrain masking, radar detection probability and track quality, burn-through, deception jamming, ESM, specular stealth model.
- **C2 and IADS:** C2 console and map, data link, SAM sites, threat ranking, tasking.
- **Campaign:**
  - factions, bases, logistics and an arsenal (who may use what);
  - an AI commander flying strike, SEAD, intercept, CAP, escort, AWACS and helicopter missions;
  - a missions board and difficulty settings.
- **Flight:**
  - ELE flight control for AI aircraft (aero-predicted, learned per axis);
  - a VTOL flight computer with an autopilot (alt AGL/ASL, speed, heading, hover);
  - FPV drones and loitering munitions.
- **Combat FX:** shrapnel, sized and typed explosions, volumetric fireballs and vapour cones, persistent scorch and fires, dynamic damage (dents, scorch marks, bent parts).
- **Parts:**
  - helicopter kit: tandem cockpit with its own IVA, fuselage, troop cabin, tail boom, rotors, stub wings, chin gun, mast radar;
  - ground kit: Dynamic Frame (procedural hull with armour), engine block, 105/120/125 mm tank guns, loaders and autoloaders, sensor ball;
  - **Dynamic Jet, Turboprop and Turboshaft:** engines you design (bypass, blades, stages, afterburner, prop, reverse thrust), with a stock texture look.
- **Cockpits:** a clickable ELE MFD, which replaces the first status-light panel in cockpits; a tandem gunship IVA; and a drone operations room.
- **Tools:** slow motion (Alt+, Alt+. Alt+/ Alt+;), aircraft range tool, IR turret editor, explosion test panel, camera reset (Ctrl+Shift+C).

Full feature notes: `GameData/BDArmoryAvionics/README.txt`. Open the ELE menu from the BDArmory toolbar.

## Notes

- The volumetric shader bundle is built for **Windows (DX11)**. On Mac and Linux, ELE falls back to normal particle effects automatically.
- The ELE MFD replaces the first `IndicatorPanel` (status lights) prop in every cockpit interior, or the docking monitor where there is one. To keep stock panels, delete `Patches/ELE_MFD_IVA.cfg`.
- Settings are saved to `GameData/BDArmoryAvionics/PluginData/Avionics.cfg`, which is created on first run.

## Licence

See `LICENSE`. BDArmory, HarmonyKSP, Module Manager and the optional mods belong to their authors and are **not** included.
