# GPU Shader Cache

*Why the **first** GPU render after a fresh install takes a moment — and why every render after that is quick.*

---

## The short version

When Fracturing Fog draws a fractal on your **graphics card** — the 2-D Mandelbrot
GPU path, and every **Relief 3-D** / volumetric scene — it runs a small program
on the GPU called a *shader*. That shader has to be **compiled** for your machine
the first time it is used. On a fast PC that compile is a second or two; on older
laptops or weak graphics drivers it can be several seconds. It only ever affected
the **first** render of a session — after that the compiled shader stayed in memory
and every render was quick.

Fracturing Fog now **saves that compiled shader to disk**. The very first time you
ever open a GPU view, it compiles once and writes the result to a cache file. Every
launch after that **loads the cached shader instead of recompiling**, so the first
render is as fast as a warm one — even after you close and reopen the app.

> [!NOTE]
> This only speeds up that **one-time first render**. It does **not** make each
> frame draw faster once things are running. If a Relief 3-D scene feels heavy
> frame-to-frame, that is the scene's lighting cost, not the cache — see the
> [Relief 3D Guide](Relief3D-Guide.md) for the knobs that trade quality for speed.

You do not have to do anything. It is on by default and takes care of itself.

---

## Where the cache lives

The cached shaders sit alongside the app's other saved data:

```
%APPDATA%\FracturingFog\ShaderCache\
```

(If you have pointed Fracturing Fog at a custom data folder, the `ShaderCache`
folder lives under that folder instead.)

The files are just compiled graphics code — safe to delete at any time. They are
**derived** data: if you remove them, the app simply recompiles on the next GPU
render and writes them again.

---

## When it refreshes itself

You never have to clear the cache by hand. It rebuilds automatically whenever it
needs to:

- **After an app update** that changes how a shader is built — the cache notices the
  shader is different and compiles the new one (the old file is ignored).
- **If a cache file is damaged** or your graphics driver rejects it — the app quietly
  throws that file away and recompiles from scratch.

Because of this, a stale or broken cache can never give you a wrong picture. The
worst it can ever cost you is one extra compile.

---

## Turning it off

You will almost never want to, but if you are diagnosing a graphics-driver problem
you can disable the cache for a run by setting an environment variable before
launching:

```
set FF_NO_SHADER_CACHE=1
```

With that set, the app compiles every shader fresh each launch (the old behaviour)
and never reads or writes the cache folder.

> [!WARNING]
> Leaving `FF_NO_SHADER_CACHE=1` set permanently just brings back the slow
> first-render on every launch. Only use it for troubleshooting, then clear it.

---

## See also

- [Relief 3D Guide](Relief3D-Guide.md) — the 3-D features that use these GPU shaders,
  and how to tune them for speed.
- [Benchmarks Guide](Benchmarks-Guide.md) — measure how fast rendering is on your machine.
