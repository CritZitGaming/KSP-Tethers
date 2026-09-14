# Changelog

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
