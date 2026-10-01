BD Armory: Enhanced Lethality Edition
=====================================
(avionics, C4ISR, IADS, air operations and combat operators for BDArmory; folder/DLL: BDArmoryAvionics)
A separate add-on for BDArmory 1.13.1.0 (KSP 1.12.5). BDArmory itself stays unmodified;
this mod hooks into it at runtime with Harmony.

Combat Operators
----------------
Open the Astronaut Complex: a Combat Operators panel lists every kerbal with five disciplines (0-5):
Pilot (steering, G tolerance, missile evasion), Gunnery (gun spread), Weapons (radar scan/lock speed),
Sensors/EW (target acquisition, countermeasure timing), Command (IADS reaction, from a crewed HQ/AWACS/EWR).
Level 2 = stock BDArmory; drones count as 2. New hires start at 1, the original four at 2.
Train: days of game time (longer for dimmer kerbals), funds in career; the kerbal is unavailable meanwhile.
Combat XP: time in guard-mode combat, kills credited to the craft's target, recovered sorties.
The best qualified crew member aboard sets each skill. Toggle: "Combat operators" in the menu.
Panel tabs: Roster (training, squadron, service record), Squadrons, Records (leaderboard incl. KIA), Battles (AARs).

Ranks: Cadet > 2nd Lt > 1st Lt > Captain > Major > Lt Colonel > Colonel > Brigadier General, from merit
(sorties, battles, kills, medals, skill levels). Never demoted.
Squadrons: create in the Squadrons tab, assign kerbals there or from the Roster tab. The commander (assigned,
or the senior member) lifts every member one level (not Command) while flying a combat craft in the same battle.
Medals: Ace / Double / Triple Ace (5/10/15 air kills), Ace in a Day (5 in one battle), Strike Medal (5 surface kills),
Gunfighter (5 gun kills), Missileer (10 missile kills), Survivor (shot down and lived), Veteran (10 battles), Master Operator.

Stealth shaping and electronic warfare
--------------------------------------
Stock BDArmory shades each surface by cos(2 x angle to the radar), so facets tilted 30° still return half: shaping
barely matters. The stealth model (menu > Electronic warfare, or the VAB/SPH C4ISR window) makes returns specular:
  Stock / Low (default) / Medium / High. Angled facets return far less, flat panels square to the radar more
  (spikes), a sphere is unchanged (BDArmory's 1 m² calibration holds), RAM / reflectivity still applies.
  BDArmory's own RCS window in the editor shows the shaped value.
  VAB/SPH "Stealth & EW analysis" (button in the C4ISR craft setup window): renders the craft under all four
  models - overall / front / side / rear / top / bottom RCS, detection and lock ranges against any radar part,
  a polar plot (stock vs shaped, level or from +-30°), plus its jammers (jamming-on signature, garble zone,
  lock-break, data link cut / degrade ranges), RWR / ESM capability and how far its own radar can be heard. Note: parts with smoothed normals look curved
  to the renderer, so their facets count as curved surfaces.
EW (Electronic warfare window):
  Link jamming   enemy jammers cut or degrade data links (strength / distance², line of sight); Sensors/EW skill
                 strengthens jamming and resists it. Jammed units drop off the network (terminal shows JAMMED).
  ESM            units with an RWR switched on locate enemy radars and jammers passively and share them as tracks
                 (ESM / ESM-JAM): rough range from one sensor, exact from two or more.
  Jammer modes   per craft (terminal part menu, editor window, EW window): Auto (BDArmory guard mode),
                 Barrage (always jamming, e.g. an EW escort), Silent (never jams).

Loitering munitions (man in the loop)
-------------------------------------
Build: probe core + engine + wings + a BDArmory warhead (+ a HullCam VDS camera for the operator's view).
On the probe core's part menu set "Loitering munition: Yes" and its strike mode, target type, loiter altitude
(sea level) / radius, cruise speed, search range, dive angle and proximity fuze. Mount it on a launcher craft.
It arms when it separates (or "Arm / launch"), flies to the launcher's GPS target (else 2 km ahead), circles and
searches for enemies of the launcher's team (Any / Ground+ships / Air / Radars for SEAD), line of sight only.
  Autonomous  strikes the first target it finds
  Approval    reports the target and circles until the operator approves (or waves off)
  Designate   strikes only what the operator designates
Terminal: level approach, then a dive at the set angle (top attack); proximity, closest-approach and impact fuzes.
Loitering munitions window (menu): approve, wave off, strike the selected contact or GPS target, move the
loiter point, watch through its camera, take control, detonate, back to the home craft.
In its camera (HUD with crosshair, target box, altitude, speed, fuel):
  Keypad Enter  strike what's under the crosshair (craft or ground point) / approve the reported target
  Keypad 5      take / give back control (you fly it with the normal controls; fuzes stay live)
  Keypad .      detonate        Keypad -   back to the home craft (also automatic after it detonates)
C2 console / map overlay: munitions show as orange triangles with their loiter ring and a line to their target
(dashed yellow = awaiting approval, red = striking). Approval requests appear at the top of the C2 panel
(APPROVE / Wave off / Watch). Select a munition for its panel: strike mode, target type, approve, wave off,
loiter at / strike a map point, strike the selected track, watch, take control, detonate. A selected hostile
track lists "Strike with LM-n" for every flying munition. A unit carrying munitions shows "Launch one: loiter at
(click map)": it releases the next munition (its decoupler) and sends it to that point.
Its kills go to the launcher's crew records (kill.LM). Keys can be changed in Avionics.cfg.

Factions & bases (needs Kerbal Konstructs; KerbinSide bases)
-----------------------------------------------------------
Theatre window (Space Center toolbar, or "Theatre" in the flight menu). Factions = BDArmory teams (name, team,
Player/AI, colour). Default: Blue Force (team A: KSC, Kerman Atoll) vs Red Force (team B, AI: Kojave Sands,
Baikerbanur). Bases tab: click a site's owner button to cycle owner; click an owned base's name for its details.
- Craft launched from a base join its owner's team. Base runways are measured when first loaded and used for autoland.
- Aircraft: station craft files at a base (authorized / available). Launching one takes an airframe, recovering it
  at a base returns it, losses are counted, replacements arrive one per 2 days (half rate when damaged).
- Fuel and ordnance: land and stop 10 s at a friendly base to refuel and re-load gun/rocket ammo from base stock
  (stock regrows daily). Missiles are not reloaded.
- Damage: BDArmory explosions within 1.5 km hurt the base (by warhead size); bombs/missiles (5 kg+) within 60 m of
  the runway close it for hours. Health repairs 25 %/day. Below 60 % Damaged, at 0 Destroyed (no runway, no service).
- Capture: armed enemy ground units at the base for 60 s with no armed defenders within 1.5 km take it.
- AI commander (Theatre > Commander): each AI faction plans on game time (Passive / Defensive / Normal 3 h /
  Aggressive 1 h). It sends Strike/SEAD-role aircraft from its bases at your nearest base and scrambles Fighter-role
  aircraft at your aircraft within 80 km of its bases. Give aircraft at its bases a role in the base panel.
  Sorties fly on game time (also in the Space Center and during warp). Within 25 km of you they become real craft
  (spawned airborne from the craft file, guard mode + pilot AI, tasked with the C2 orders) and fight for real;
  away from you a strike is resolved by numbers (base defences = stationed Fighters). Losses come out of the
  inventory, survivors return. Detected sorties (your radar or 30 km of a unit) show on the C2 map as diamonds.

Combat search and rescue (CSAR)
-------------------------------
Ejecting (P-P or auto-eject) from a combat craft reports the pilot to C2: a GPS point on their team named
"CSAR <rank> <name> (<aircraft>)", moved to where they land. The C2 panel lists downed pilots at the top
(time down, capture countdown) with "GPS target" and "Send <selected unit>"; the C2 map marks them with a red cross.
- Territory: nearer an enemy base than a friendly one = enemy territory. An enemy search team arrives after
  1 h + 1 h per 100 km from that base (game time, also while warping). Friendly aircraft within 3 km hold it off
  30 min at a time. Armed enemy ground units within 150 m for 15 s capture the pilot on the spot.
- Pick up: the pilot boards any friendly craft (walk to the hatch, winch, or land / hover below 20 m within 60 m
  with a free seat and they climb in). Rescued when that craft stops at a friendly base or is recovered.
- Captured pilots are POWs (missing, unavailable) held at the nearest enemy base; capturing that base frees them.
- Records: ejections, rescued, POW, freed, rescues made; medals Rescue Medal, Angel (5 rescues), Evader, POW Medal.

Kill log and after-action report
--------------------------------
Every BDArmory hit (guns, rockets, missiles, nukes, fire, rams) is credited to the craft that caused it. A craft is
killed when it is destroyed, loses its weapons manager or command parts, or its crew bail out / die; the last
hostile damage in the previous 30 s gets the kill (or whoever was targeting it, for a manoeuvre kill).
Kills are classed A-A, A-G, A-S (ships), G-A, G-G... (A air, G ground, S sea, O space), manned / unmanned,
and by weapon, in every crew member's service record. Friendly fire and unattributed losses are logged too.
A battle starts at the first hit and ends 45 s after the last; the after-action report (teams, kills, losses,
MVP, each unit's hits / damage / result, timeline) opens in flight, is kept with the save (last 25, Battles tab)
and written to PluginData/AAR/*.txt. Menu: "After-action rpt" (live during a battle), "AAR at battle end".

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

Pilot AI air operations
-----------------------
Formation assist: wingmen hold the leader's bank and fix sideways error with limited bank, height with pitch
(no inverted pulls), speed damped by closure rate. Takeoff assist: holds runway heading and centreline on the
roll (wheel steering + rudder, wings level), rotates at 3°/s to 10° max. C2 panel AIR OPS:
  Land    approach, 3.5° glideslope, flare, brake, stop (KSC runway built in; add your own with two GPS
          points named "<name> RWY A" and "<name> RWY B" on the runway ends). Go-around if unstable.
  Refuel  (AARS) join behind the nearest friendly tanker, stabilise, plug the probe into the drogue,
          take fuel, disconnect. Tanker: straight and level at constant speed with drogues deployed.
"AI telemetry log" in the BDA Avionics menu writes formation / landing / refuelling state to KSP.log.

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
