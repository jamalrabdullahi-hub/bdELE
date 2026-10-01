BDArmory Avionics & C4ISR
=========================
A separate add-on for BDArmory 1.13.1.0 (KSP 1.12.5). BDArmory itself stays unmodified;
this mod hooks into it at runtime with Harmony.

Requires: BDArmory, Harmony (000_Harmony), ModuleManager.
Optional: SCANsat (scanned-coverage maps).

Install: copy this BDArmoryAvionics folder into GameData.
Remove: delete GameData/BDArmoryAvionics. Craft files keep working (the terminal module is just dropped).
Replaces BD Targeting Expansion: do not install both (they share the toolbar, camera window and P-P eject).
Settings are read once from BDTargetingExpansion/PluginData/Settings.cfg or BDArmory/PluginData/Avionics.cfg if this mod has none yet.

In flight (toolbar button "BDA Avionics", or Right Alt + key)
-------------------------------------------------------------
  3D Radar, Target List, GPS Manager, FLIR / pod HUD (bottom of the Target Camera window)
  C2 Console        Right Alt + C   map, unit roster, track table, tasking
  Map overlay       (map view)      units, tracks, data links, SAM rings + C2 drawer
  IADS              Right Alt + I   SAM sites, engagement matrix, doctrine, EMCON
  Data link         Right Alt + L   link model, node paths and quality
  Settings          link model, track timing, map source, overlays
  Ejection seat     press P twice; auto-eject options in the menu
  Threat warning    RWR strobes on the 3D radar, banner for launches/locks/inbound missiles (needs an RWR)
  Weapon estimate   3D radar shows Rmax / time of flight / IN RANGE for the selected missile vs selected target
  CM program        Keypad 0 runs the craft's countermeasure program (Auto mode runs it on inbound missiles)

Missiles
--------
Flight estimator: simulates each missile (thrust, fuel burn, BDArmory drag, air density, gravity) for
launch zones, IADS intercept times and site selection. Modular missiles are estimated from their parts (est.).
Modular missiles can be mounted by any part (e.g. the booster): guidance follows the stage that carries it.
Flight profile (modular missile part menu): per-stage guidance switching, and an ICBM profile
(vertical rise, ballistic boost, engine cutoff when the predicted impact reaches the target, coast,
then the missile's own guidance for the terminal dive). Use GPS targeting and a throttleable last stage.
Data link mid-course: radar missiles keep getting target updates from any networked sensor.

Infernal Robotics turrets (optional, needs Infernal Robotics Next)
------------------------------------------------------------------
Every IR servo gets a "BDA turret control" part-menu section. Put a turret's rotating servos (e.g. a rotator
for yaw and a hinge for pitch) in the same turret group and mount a gun, missile launcher or camera at the tip.
The group aims it at: the guard-mode target (using BDArmory's own lead and bullet drop), the radar lock, the
targeting camera's point, or the selected contact ("Auto" = first available in that order).
In guard mode BDArmory fires the gun once it's on target, including against aircraft from the ground.
Each servo learns its turning direction in the first second of movement ("learning" in its status).

Tasking (console or map drawer): select a unit, then Patrol (click waypoints, Esc to finish),
Move to, Strike GPS, Intercept (select a track first), Escort the active vessel, RTB, Release.
ROE: Hold fire / Weapons free (= guard mode off / on). SAM sites must be in guard mode for IADS.

Map clicks: left = select or place the armed order, right (console) = drop a GPS point,
drag = pan, wheel = zoom.

Data link models
  Global C2       whole team linked, no range limit (default)
  CommNet         linked when CommNet can reach home (relays count)
  Line of sight   linked within range when the planet isn't in the way

In the VAB / SPH
----------------
Every command part and weapon manager gets a "C4ISR data link" section in its part menu:
role (Auto works it out), data link on/off, IADS member, radar EMCON override.
The "C4ISR craft setup" toolbar button shows the craft's sensors, missiles, antennas,
KerbNet parts and ejection seats, the role it will get, and warnings.
Weapon managers also get a "Countermeasure program" section (Off/Manual/Auto, flares and chaff per burst,
bursts, salvos, intervals, trigger time) and modular missile guidance parts a "Missile flight profile" section.

Settings file: GameData/BDArmoryAvionics/PluginData/Avionics.cfg
