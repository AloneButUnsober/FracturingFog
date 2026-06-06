# ColorGen — User Guide

ColorGen turns a short DSL expression into a fully-functional
algorithmic colour theme. Every pixel's escape data is exposed as
named inputs; the DSL evaluates to a `Vec3` colour in `[0, 1]^3` and
the runtime packs that into ARGB. Output is a sealed `IColorMap`
subclass that joins the live palette list immediately (Compile & Load)
or ships as a permanent C# file (Generate via ColorGen).

Open the editor from the render surface's **right-click → ColorGen
Editor…** menu.

---

## 1. Quick start

1. Open **ColorGen Editor…**.
2. Type a DSL program. The default seed:
   ```cg
   let h = smooth * 0.03;
   let s = 0.85;
   let v = isInSet > 0.5 ? 0.3 : 1.0;
   return hsv(h, s, v);
   ```
3. Set the **Theme name** and (optional) **Category** / **Description**.
4. **Compile & Load** — the live render switches to the new theme.
5. **Save…** to persist the source (under
   `%APPDATA%\FracturingFog\colorgen.json`).
6. **Generate via ColorGen** to emit a permanent class under
   `Models/ColorSchemes/Generated/{Name}Theme.cs` — rebuild to ship.

The editor enforces one rule: the program must end with **exactly one**
`return <vec3>;`. Use `rgb`, `hsv`, `hsl`, or `palette` to produce the
final vec3.

---

## 2. Language reference

### 2.1 Statements

```
let <name> = <expr>;       // bind a local (Scalar or Vec3)
return <vec3-expr>;        // last statement; must be Vec3
```

`let` names cannot shadow built-in inputs or constants. Comments use
`//` (line) or `/* … */` (block).

### 2.2 Types

| Type    | Meaning                  | Channel access |
|---------|--------------------------|----------------|
| Scalar  | `double`                 | n/a            |
| Vec3    | RGB triple, each `[0,1]` | `.r .g .b`     |

Binary `+ - * / % ^` auto-broadcast scalar↔vec3 (the result is Vec3
when either side is Vec3). Comparisons and logical ops require scalar
operands and yield `1.0` / `0.0`.

### 2.3 Inputs (read-only built-ins)

| Name        | Type   | Meaning                                              |
|-------------|--------|------------------------------------------------------|
| `smooth`    | scalar | Smooth iteration count at escape                     |
| `dist`      | scalar | Exterior distance estimate (0 inside set)            |
| `iter`      | scalar | Iteration count at escape (or maxIter for in-set)    |
| `maxIter`   | scalar | Max iterations for this frame                       |
| `t`         | scalar | `smooth / maxIter` — convenience normalised [0, 1]   |
| `nx, ny`    | scalar | Surface-normal components in `[-1, 1]`               |
| `zr, zi`    | scalar | Final `z` at escape                                  |
| `dzr, dzi`  | scalar | Final `dz/dc` at escape                              |
| `arg`       | scalar | `atan2(zi, zr)` (radians)                            |
| `mag`       | scalar | `hypot(zr, zi)` = `|z|`                              |
| `isInSet`   | scalar | `1.0` if `iter >= maxIter`, else `0.0`               |
| `pxScale`   | scalar | Complex-plane width of one pixel (1.0 if unset)      |

### 2.4 Constants

`pi`, `tau` (= `2π`), `e`, `phi` (golden ratio).

### 2.5 Operators (precedence high → low)

| Group        | Operators        | Notes                          |
|--------------|------------------|--------------------------------|
| Postfix      | `.r .g .b`       | Channel access on Vec3         |
| Unary        | `- + !`          | `!x` is `1.0` iff `x == 0`     |
| Power        | `^`              | Right-associative              |
| Multiplicative | `* / %`        | `%` is GLSL-style `mod`        |
| Additive     | `+ -`            |                                |
| Comparison   | `< <= > >= == !=`| Scalar; yield `1.0` / `0.0`    |
| Logical AND  | `&&`             | Scalar                         |
| Logical OR   | `\|\|`           | Scalar                         |
| Ternary      | `?:`             | Branches must match types      |

### 2.6 Built-in functions

#### Scalar → Scalar
`sin cos tan asin acos atan sinh cosh tanh exp log log2 log10 sqrt abs
sign floor ceil round fract saturate radians degrees`

#### Two-argument scalar
`atan2(y,x) hypot(x,y) min(a,b) max(a,b) mod(x,y) pow(x,e) step(edge,x)`

#### Three-argument scalar
`clamp(x, lo, hi) smoothstep(edge0, edge1, x)`

#### Scalar `mix`
`mix(a, b, t)` — linear interpolation.

#### Hash / noise
`hash(x)` — pseudo-random scalar in `[0,1)` from a single input.
`hash2(x, y)` — two-input version.

#### Vec3 constructors
| Form                  | Description                                    |
|-----------------------|------------------------------------------------|
| `rgb(r, g, b)`        | Direct linear RGB (each in `[0,1]`)            |
| `hsv(h, s, v)`        | Hue is cyclic (`fract` is applied for you)     |
| `hsl(h, s, l)`        | Same hue convention                            |

#### Vec3 operations
| Form                          | Description                                 |
|-------------------------------|---------------------------------------------|
| `mix(va, vb, t)`              | Polymorphic — picks Vec3 form when args are |
| `palette(t, c0, c1, c2, …)`   | Cyclic n-stop palette evaluated at `t`      |
| `brightness(v, s)`            | Add `s` to each channel                     |
| `contrast(v, s)`              | Around 0.5; `s` in `[-1, 1]`                |
| `gamma(v, g)`                 | `pow(channel, 1/g)`                         |

### 2.7 Output packing

Final `return <vec3>;` clamps each channel to `[0, 1]` and packs as
opaque ARGB. There is no separate alpha; the colour map's interior
override is handled by the host (via `isInSet`).

---

## 3. Examples

Every example below is a complete program — paste verbatim into the
editor and Compile & Load.

### 3.1 Pure HSV cycler

```cg
return hsv(smooth * 0.04, 0.9, 1.0);
```

### 3.2 HSV with in-set override

```cg
let v = isInSet > 0.5 ? 0.3 : 1.0;
return hsv(smooth * 0.05, 0.85, v);
```

### 3.3 Sinusoidal RGB

```cg
let k = smooth * 0.1;
return rgb(
  0.5 + 0.5 * sin(k),
  0.5 + 0.5 * sin(k + tau / 3),
  0.5 + 0.5 * sin(k + 2 * tau / 3));
```

### 3.4 Cyclic palette

```cg
return palette(
  smooth * 0.02,
  rgb(0.05, 0.02, 0.10),
  rgb(0.40, 0.10, 0.55),
  rgb(0.95, 0.55, 0.10),
  rgb(1.00, 0.95, 0.70));
```

### 3.5 Banded gradient

```cg
let k = fract(t * 8.0);              // 8 bands across [0, 1]
return palette(k,
  rgb(0, 0, 0),
  rgb(1, 0.4, 0),
  rgb(1, 1, 0.7),
  rgb(0.2, 0.6, 1));
```

### 3.6 Distance-field glow

```cg
let d = tanh(dist / pxScale * 0.5);
let core = rgb(1.0, 0.95, 0.6);
let halo = rgb(0.1, 0.3, 0.9);
return mix(halo, core, smoothstep(0.0, 1.0, d));
```

`dist / pxScale` converts the raw complex-plane distance estimate into pixel
units, so the halo width stays constant at every zoom level. `tanh` gives a
smooth saturation curve — no hard edge where the glow flattens out.

### 3.7 Slope (Lambert) shading

```cg
// Light from upper-right; nx,ny are -1..1.
let lx = 0.4;
let ly = -0.4;
let lz = 0.8;
let nzNorm = 1.0;                         // implicit z component
let dotN = nx*lx + ny*ly + nzNorm*lz;
let lit = clamp(dotN, 0.0, 1.0);
let base = hsv(smooth * 0.03, 0.6, 1.0);
return brightness(base * lit, 0.05);
```

### 3.8 Argument coloring (domain coloring)

```cg
return hsv(arg / tau, 1.0, isInSet > 0.5 ? 0.3 : 1.0);
```

### 3.9 |z| chrome bands

```cg
let band = fract(log(mag) * 4.0);
let v = 0.4 + 0.6 * band;
return rgb(v, v, v);
```

### 3.10 Two-tone toon

```cg
let lit = nx*0.5 + ny*0.5 + 0.5;          // crude shade [0,1]
return lit > 0.6 ? rgb(1, 1, 1) : rgb(0.05, 0.05, 0.20);
```

### 3.11 Stripes from iter

```cg
let stripe = sin(smooth * pi / 4.0);
let base = palette(t,
  rgb(0.10, 0.10, 0.20),
  rgb(0.95, 0.25, 0.40),
  rgb(1.00, 0.85, 0.30));
return brightness(base, 0.10 * stripe);
```

### 3.12 Procedural noise

```cg
let n = hash2(floor(smooth), floor(t * 50.0));
let hue = fract(t * 3.0 + 0.1 * n);
return hsv(hue, 0.85, 0.9);
```

### 3.13 Field-line emphasis

```cg
let edge = smoothstep(0.0, 1.0, abs(sin(smooth * pi)));
let body = hsv(t * 3.0, 0.7, 0.9);
return brightness(body, -0.3 * edge);
```

### 3.14 Inside-set highlight

```cg
let outside = palette(smooth * 0.03,
  rgb(0,0,0), rgb(0.3,0.0,0.5), rgb(1,1,1));
let inside = rgb(0.0, 0.4, 0.6);
return isInSet > 0.5 ? inside : outside;
```

### 3.15 Phong-ish three light blend

```cg
let lit1 = clamp(nx*0.5 + ny*-0.5 + 0.7, 0.0, 1.0);
let lit2 = clamp(nx*-0.4 + ny*0.4 + 0.3, 0.0, 1.0);
let key = rgb(1, 0.95, 0.85);
let fill = rgb(0.2, 0.35, 0.7);
let c1 = key * lit1;
let c2 = fill * lit2 * 0.5;
let base = palette(t * 2,
  rgb(0.02, 0.02, 0.08),
  rgb(0.8, 0.6, 0.3),
  rgb(1, 1, 1));
return base * 0.5 + c1 + c2;
```

### 3.16 Cycling palette + lighting hybrid

```cg
let lit = clamp(nx*0.4 + ny*-0.4 + 0.5, 0.0, 1.0);
let p = palette(smooth * 0.015,
  rgb(0.00, 0.00, 0.05),
  rgb(0.10, 0.20, 0.60),
  rgb(0.95, 0.85, 0.30),
  rgb(1.00, 0.40, 0.20));
return brightness(p * lit, 0.04);
```

### 3.17 Power-law gamma

```cg
let base = palette(smooth * 0.02,
  rgb(0, 0, 0),
  rgb(1, 0.3, 0.1),
  rgb(1, 1, 1));
return gamma(base, 1.8);
```

### 3.18 Contrast-pumped grayscale

```cg
let g = saturate(smooth * 0.005);
return contrast(rgb(g, g, g), 0.6);
```

### 3.19 Hue-rotated escape phase

```cg
let phase = arg / tau + 0.5;             // [0, 1]
let hue = fract(phase + 0.15 * sin(t * tau));
return hsv(hue, 0.85, isInSet > 0.5 ? 0.3 : 1.0);
```

### 3.20 Layered psychedelia

```cg
let a = hsv(smooth * 0.04, 0.8, 1.0);
let b = hsv(smooth * 0.04 + 0.5, 0.8, 1.0);
let mixT = 0.5 + 0.5 * sin(t * tau * 3.0);
return mix(a, b, mixT);
```

### 3.21 Channel-shifted RGB

```cg
let k = smooth * 0.05;
let r = 0.5 + 0.5 * sin(k);
let g = 0.5 + 0.5 * sin(k + 1.0);
let b = 0.5 + 0.5 * cos(k * 1.3);
return rgb(r, g, b);
```

### 3.22 |dz/dc| highlight

```cg
let mag2 = sqrt(dzr*dzr + dzi*dzi);
let glow = saturate(log(1 + mag2) * 0.2);
let base = palette(smooth * 0.02,
  rgb(0.05, 0.05, 0.10),
  rgb(0.40, 0.20, 0.80),
  rgb(1.00, 0.95, 0.40));
return brightness(base, 0.3 * glow);
```

### 3.23 Threshold-banded posterise

```cg
let raw = saturate(smooth * 0.005);
let q = floor(raw * 6.0) / 5.0;
return hsv(q, 0.8, 1.0);
```

### 3.24 Interior cycle painter

```cg
let outside = palette(smooth * 0.03,
  rgb(0, 0, 0), rgb(0.5, 0, 0.5), rgb(1, 1, 1));
let inside = hsv(arg / tau, 0.7, 0.6);
return isInSet > 0.5 ? inside : outside;
```

### 3.25 Wood grain

```cg
let r = mag * 8.0;
let g = fract(r + sin(arg * 6.0) * 0.2);
let base = mix(
  rgb(0.30, 0.18, 0.08),
  rgb(0.78, 0.55, 0.25),
  g);
return base;
```

### 3.26 Holographic interference

```cg
let f1 = sin(smooth * 0.5);
let f2 = sin(smooth * 0.5 + arg * 3.0);
let mixT = 0.5 + 0.5 * (f1 * f2);
let a = rgb(0.10, 0.50, 1.00);
let b = rgb(1.00, 0.30, 0.70);
return mix(a, b, mixT);
```

### 3.27 Heatmap

```cg
let t01 = saturate(smooth * 0.003);
return palette(t01,
  rgb(0.00, 0.00, 0.10),
  rgb(0.30, 0.00, 0.50),
  rgb(0.90, 0.20, 0.00),
  rgb(1.00, 0.90, 0.20),
  rgb(1.00, 1.00, 1.00));
```

### 3.28 Aurora

```cg
let band1 = sin(t * tau * 4 + nx * 6);
let band2 = sin(t * tau * 7 + ny * 9);
let mixT = 0.5 + 0.25 * band1 + 0.25 * band2;
return mix(
  rgb(0.05, 0.10, 0.20),
  rgb(0.10, 1.00, 0.40),
  saturate(mixT));
```

### 3.29 Plasma

```cg
let p = sin(smooth * 0.05) + sin(arg * 4.0) + sin(mag * 3.0);
let q = fract((p + 3.0) * 0.16667);
return palette(q,
  rgb(0.05, 0.00, 0.30),
  rgb(0.80, 0.10, 0.50),
  rgb(1.00, 0.85, 0.40),
  rgb(0.95, 1.00, 0.95));
```

### 3.30 Vintage sepia

```cg
let g = saturate(smooth * 0.005);
let warm = rgb(g * 1.10, g * 0.95, g * 0.70);
return gamma(warm, 1.4);
```

---

## 4. Compile & Load vs Generate via ColorGen

| Path                       | When to use                                                |
|----------------------------|------------------------------------------------------------|
| **Compile & Load**         | Iterative tuning. Theme lives until you close the app.    |
| **Save…**                  | Persist the **DSL source** between sessions.              |
| **Generate via ColorGen**  | Promote keepers to a permanent C# file you commit + ship. |

Generated files land at
`Models/ColorSchemes/Generated/{Name}Theme.cs`. A `dotnet build` of the
main project picks them up via the default glob; the theme then appears
in every theme combo under its **Algorithmic** kind.

---

## 5. Persistence

`%APPDATA%\FracturingFog\colorgen.json` stores `Name + Source +
Description` tuples. Edit the file with any text editor — invalid
entries are silently dropped on load (no app crash). Use **Save…** to
ensure the JSON regenerates cleanly.

---

## 6. Troubleshooting

| Message                                            | Cause                                                    |
|----------------------------------------------------|----------------------------------------------------------|
| `'return' must yield a Vec3 …`                     | Wrap the final value with `rgb` / `hsv` / `hsl` / `palette`.|
| `Stray tokens after 'return' …`                    | `return` must be the last statement.                     |
| `Unknown identifier 'foo' …`                       | Typo or unsupported name. Check input list.              |
| `Ternary branches must have matching types …`      | Both `?:` arms must be both Scalar or both Vec3.         |
| `palette() arg 1 must be scalar …`                 | First palette arg is `t`; stops come after.              |
| `palette() stops must be Vec3 …`                   | Use `rgb`/`hsv`/`hsl` for each stop.                     |
| `Channel access requires a Vec3 …`                 | `.r/.g/.b` only on Vec3 values.                          |
| Compile failed CSxxxx (after Compile & Load)       | Generated C# rejected by Roslyn — error includes line/col. |
| Theme picks up but render unchanged                | Some calculators cache; pan/zoom once to force recolor.  |

---

## 7. Reference card

```
Inputs    smooth dist iter maxIter t nx ny zr zi dzr dzi arg mag isInSet pxScale
Const     pi tau e phi
Ctors     rgb(r,g,b) hsv(h,s,v) hsl(h,s,l)
Palette   palette(t, c0, c1, …)                  // n cyclic stops
Mix       mix(a,b,t)                             // scalar or vec3
Color FX  brightness(v,s) contrast(v,s) gamma(v,g)
Math      sin cos tan asin acos atan sinh cosh tanh exp log log2 log10
          sqrt abs sign floor ceil round fract saturate radians degrees
          atan2 hypot min max mod pow step clamp smoothstep
Hash      hash(x) hash2(x,y)
Ops       + - * / % ^ < <= > >= == != && || ! ?:
Channels  .r .g .b
Stmts     let name = expr;     return vec3-expr;
```

The DSL grammar is small enough to memorise; this card plus the example
gallery in §3 covers virtually every "I want a theme that does X"
scenario.

---

## 8. See Also

- [ColorThemeEditor-Guide.md](ColorThemeEditor-Guide.md) — Stops / Phong / PBR3D editor for non-DSL theme authoring
- [Avalonia-UserGuide.md](Avalonia-UserGuide.md) — UI walkthrough including the ColorGen editor
- [CalcGen-UserGuide.md](CalcGen-UserGuide.md) — sibling DSL for algorithmic fractal equations
- [Architecture-Overview.md](Architecture-Overview.md) — where ColorGen sits in the solution
- [Capture-Guide.md](Capture-Guide.md) — using ColorGen output in posters / videos
