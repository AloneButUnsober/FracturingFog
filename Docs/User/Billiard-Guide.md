# Chaotic Billiards and Billiard Rays

Fracturing Fog has two ways to look at the same thing: rays of light bouncing between mirrored disks until
they escape.

- **Chaotic Billiard (Scatter)** maps *where rays start*. Every pixel is one ray; its colour says which gap it
  finally leaves through. Zoom in on a boundary between colours and it never gets simpler: that is the
  fractal.
- **Billiard Rays (Ray Density)** looks down on the disks *in real space* and draws the rays themselves:
  thousands of them, their paths, where they cross each other, and where neighbouring rays part company.

Both use the same mirrors, so a layout you build in one shows up in the other. Background and sources: the
R&D notes ([Theoretical Fractal R&D §2.5](../Technical/Theoretical-Fractal-RnD.md)) and the bibliography.

---

## 1. The mirrors (both types)

In the fractal parameters panel:

| Setting | What it does |
|---|---|
| **Geometry** | `ThreeDisk` (the classic scatterer), `Ring` (disks evenly on a circle), `NDisk` (a seeded random field), `Custom` (your own layout, see §2). |
| **Disk count / radius / separation / seed** | Size and spacing of the generated layouts. A yellow note appears when ring neighbours touch, which closes the gaps. |
| **Max bounces** | A ray still inside after this many reflections counts as *trapped*. |
| **Gate mode / count** | `ExitGap`: the gates are the gaps between disks. `Sector`: equal compass sectors. |

## 2. The Ray paths window

**Ray paths...** in the panel (or Control Center → View → **Ray Paths**) opens a small window showing the disks
from above.

- In **Chaotic Billiard**, hover the render to see that pixel's ray; **Ctrl+click** pins up to four rays, so
  you can compare neighbours on either side of a colour boundary.
- In either type, **edit the layout**:
  - **drag** a disk to move it;
  - **mouse wheel** over a disk resizes it;
  - **double-click** empty space adds a disk;
  - **right-click** a disk removes it.

  The first edit switches the geometry to `Custom`. The window updates as you drag, and the render follows when
  you let go. Disks always keep a small gap between them.

The window is never part of a render or an export.

## 3. Billiard Rays

### Sweeps: how the rays come in

| Sweep | Rays |
|---|---|
| `BSweep` (default) | Parallel beams with the offset swept across the circle. **Beams** sets how many directions, spaced evenly from **Angle**. |
| `AngleSweep` | An emitter going round the circle, aiming inward within **Spread**. |
| `PointSource` | A fan from (**Source X**, **Source Y**) over **Spread**, centred on **Angle**. |
| `RandomFarField`, `PhaseSpace` | Random rays from outside, or random rays leaving disk 0 (**Ray seed**). |

**Rays** is the total count. **Min bounces** hides rays with fewer reflections (2 hides straight misses and
single glances). **Sweep progress** draws only the first part of the sweep. Animate it from 0 to 1 to watch the
rays arrive in order.

### What to draw

| Draw | Picture |
|---|---|
| `Paths` | How much ray path crosses each pixel (log scale). **Colour by** picks the hue: the theme gradient, the **Gate** each ray leaves through, or a per-ray value (bounces, path length, entry / exit angle, gates between entry and exit, self-crossing). |
| `Crossings` / `Both` | Points where a ray crosses one of the rays fired just before it (**Crossing window**). **Crossing colour**: count, bounce difference (`DeltaN`), angle, or path length from entry / to exit. `Both` draws them over the paths. |
| `Divergence` / `DivergenceOverPaths` | Where each ray parts from the ray launched next to it. With **Divergence colour** = `GateChange` the lines are the boundary between exit basins, in real space. Random sweeps have no neighbours. |
| `Escaped` | Rays that leave without crossing any earlier ray in the window are drawn (**Escaped colour**); the rest show only their crossings. The status bar counts escaped / crossing / trapped rays. Few rays escape a mirror array: a reflected ray nearly always crosses its neighbour's incoming path. |

Crossings, divergence and the colour values can also drive **Relief 3D** as height.

**Speed.** Most of the time goes into drawing the paths. 200 000 rays at 1920x1080 take about 0.3 s (release
build). Large crossing windows cost more because the number of crossings grows fast. While you drag, a fast
low-resolution preview stands in.

### Presets

Open the regions list and pick one:

- **Billiard Rays - Three-Disk Gate Flow:** three beams, every ray in its exit gap's colour.
- **Billiard Rays - Ring Crossings:** six mirrors, rays from all round, crossings coloured by bounce
  difference, with relief.
- **Billiard Rays - Custom Five-Disk Divergence:** an irregular layout with the exit-basin boundary drawn over
  the paths. Edit it in Ray paths.

## 4. Batch rendering

Everything above renders headless. A preset:

```bash
FracturingFog.exe --batch --region "Billiard Rays - Three-Disk Gate Flow" --width 1920 --height 1080 --out gate-flow.png
```

Any setting, through `--param` (the same names the regions store):

```bash
FracturingFog.exe --batch --fractal BilliardRays --x 0 --y 0 --zoom 0.6 --width 1920 --height 1080 --param BilliardRaysCount=200000 --param BilliardRaysAngleCount=3 --param BilliardRaysAngle=90 --param BilliardRaysColorSource=1 --out three-beams.png
```

A custom layout (`x,y,r` per disk, separated by `;`):

```bash
FracturingFog.exe --batch --fractal BilliardRays --x 0 --y 0 --zoom 0.6 --width 1600 --height 1200 --param BilliardGeometry=3 --param "BilliardCustomDisks=0,1.1,0.55;1.05,0.2,0.5;0.55,-1,0.6;-0.75,-0.85,0.45;-1.1,0.35,0.5" --param BilliardRaysMode=4 --param BilliardRaysDivergenceValue=1 --out custom.png
```

Enum settings take their number in the order the panel lists them, for example `BilliardRaysMode` 0 = Paths,
1 = Crossings, 2 = Both, 3 = Divergence, 4 = DivergenceOverPaths, 5 = Escaped. The Control Center **Command**
builder writes the full command for whatever is on screen.
