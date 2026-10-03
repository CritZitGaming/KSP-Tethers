# Changelog

## 1.1.0

- **Works with Principia.** Principia integrates every vessel itself and writes the result over whatever PhysX
  worked out, so a joint between two vessels is thrown away and tethers never pulled: they hung slack and
  stretched without limit. Tethers can now hold on by adding a force to the part at each end instead, which
  Principia reads and keeps. That happens automatically when Principia is installed; the toolbar app's Setup
  tab can force either method. The pull is a soft constraint, so it matches the old spring exactly for ordinary
  loads and cannot catapult a kerbal however stiff, heavy or slow the situation gets. A tether whose ends keep
  drifting apart while it is supposed to be holding now lets go instead of drawing a line to infinity.
- **New cable surfaces.** Every style is generated from its real construction: a corrugated hose with a helical
  rib, tubular webbing woven in a 2/2 twill, sixteen-carrier diamond braid, and six-strand lang-lay wire rope
  with grease in the valleys, each with cavity shading baked in and a specular map. The maps are twice the
  resolution and the relief is real rather than a tinted pattern.
- **Flat tethers.** Two new styles, **Blue Flat Tether** (herringbone weave with woven-in stripes, in the
  Russian style) and **White Flat Tether**, are flat 25 mm straps rather than round cables: the mesh has a flat
  cross-section and the weave wraps round it so the selvedge edges land on the strap's real edges.
- **Cable lying on the ground beds in.** A cable that has lain still on the surface for a moment is held
  exactly where it lies and is no longer offered to the collision solver at all, so a rover driving over it or
  a kerbal walking along it passes by without dragging it out of shape. The rope's own pull still lifts it:
  reeling, or picking an end up, breaks the grip and the cable comes free as normal. Tune or switch it off
  with `surfaceCutouts`, `settleTime` and `settleGrip` in `Settings.cfg`. A bedded cable also stops the faint
  shimmer a resting rope used to have.
- **Tether points.** Docking ports, claws, ladders, crewed parts and - with KAS installed - winches, ports and
  pylons gain a tether point. A kerbal can clip on with one right-click, and the cable leaves a KAS part from
  the same socket KAS runs its own cable from.
- **Cables rigged in the editor.** Pick two tether points in the VAB or SPH, choose "Rig Cable From Here" on
  one and "Rig Cable To Here" on the other, and set the length: the craft launches with the cable already
  strung between them, as slack as you like. The length can be changed in flight too.
- **Keys for ship-to-ship cables.** One cable at a time is selected - in the app, with next/previous keys, or
  with a key bound to that cable's own slot - and the ordinary reel keys drive it whenever you aren't flying a
  tethered kerbal. There is also a key to release the selected cable. A kerbal on EVA still owns the reel keys
  for their own tether.
- **Cheats.** Difficulty Settings > KSP Tethers > Cheats: infinite length, a reel-speed multiplier, longer
  reach, a tether-strength multiplier, unbreakable tethers, and crazy physics that turns them into bungee
  cords. All off until the master switch is ticked.
- The app has a Setup tab showing which of Principia, KAS, ToolbarControl and ClickThroughBlocker were found,
  and what any cheats are doing.
- `CABLE_STYLE` nodes have changed with the new surfaces: `construction` replaces `pattern` (the old names
  still work), the generator's settings moved into a `TEXTURE` sub-node, and `gloss` replaces `shininess`,
  `specular` and `bump`. A patch written against the old keys will still load, but will no longer change the
  look; see the comments in `Settings.cfg`.

## 1.0.0

First release.

- **Simulated rope.** A position-based (Verlet) rope: inextensible, with bending stiffness and a minimum bend
  radius so slack floats in lazy loops rather than crumpling, static and sliding friction, air drag in
  atmosphere, and gravity that is correct in every frame of reference (floats in orbit, hangs when landed,
  swings back during burns). Drawn as a smooth tube with metal fittings at both ends.
- **Collisions.** The rope rests on terrain and drapes over parts and kerbals. Contacts for nodes and for
  segment midpoints are solved together with rope length, so a taut rope neither cuts through a hull nor
  across its edges. The rope collides with itself.
- **EVA tethers.** Kerbals leaving a hatch in space are clipped to the hull beside the hatch (configurable).
  Point and click to clip onto any part in reach, or onto another kerbal. Tap the tether key to clip your free
  end somewhere else, hold it to release.
- **Physical link.** Once the tether runs out of slack, a soft spring-damper tuned to the masses on each end
  holds the kerbal. When the rope wraps around the ship, it pulls from where it touches the hull, so it can't
  be dragged through the ship.
- **Reel.** Rope pays out automatically as a kerbal drifts away, keeping loose loops like real umbilicals.
  Reel in to haul a kerbal back to the hatch, including from the ship to rescue a kerbal who is out of EVA
  propellant.
- **Cables between ships.** Clip the free end of a tether onto another vessel to rig a cable between the two.
  Cables are saved with the game and can be reeled, released, picked up again, and set to share resources.
- **Lifeline.** Tethers carry ElectricCharge and life support (TAC Life Support, Kerbalism and other mods that
  keep resources in EVA suits) into the suit and waste back to the ship. Kerbal-to-kerbal tethers share
  supplies. Optional jetpack refuelling from the ship's MonoPropellant.
- **Toolbar app** on the stock launcher and/or Blizzy's toolbar (through ToolbarControl when installed):
  manage every tether and cable, choose cable styles, switch the lifeline and individual resources, and rebind
  keys.
- **Seven cable styles** with procedurally generated textures: white umbilical, white fabric webbing,
  gold-silver braid, silver braid, steel wire rope, hi-vis safety line and black rubber hose. Separate styles
  for EVA tethers and ship cables, adjustable thickness, and custom styles through `CABLE_STYLE` nodes.
- **Settings.** Per-save options in the difficulty settings; global tuning, cable styles and lifeline
  resources in a ModuleManager-patchable `Settings.cfg`.
