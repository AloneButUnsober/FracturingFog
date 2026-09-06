// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Services/LookStore.cs
//
// Roadmap slice S10-LW.4c (PaletteBuilder-Design.md §4a, #392 / #695) — save / recall
// custom scene "looks". Persists user-authored Looks (ramp + material + light tints) to a
// single JSON file under the app-data root, so a look derived from a palette can be
// recalled later and re-applied to the render (LW.4b). Lives in UI.Avalonia (reachable by
// the base palette VM); serialises through a primitive DTO because Look's colour tuples
// don't round-trip cleanly through System.Text.Json.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FracturingFog.Abstractions;
using FracturingFog.Imaging;

namespace FracturingFog.UI.Avalonia.Services;

/// <summary>Loads / saves custom <see cref="Look"/>s to a JSON file under the app-data
/// root (roadmap S10-LW.4c, #695).</summary>
public sealed class LookStore
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private readonly string _path;

    public LookStore() : this(AppDataPaths.Combine("palette-looks.json")) { }

    public LookStore(string path) => _path = path;

    /// <summary>All saved looks (empty on first run or a corrupt / unreadable file).</summary>
    public List<Look> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<Look>();
            var dtos = JsonSerializer.Deserialize<List<LookDto>>(File.ReadAllText(_path), s_json)
                       ?? new List<LookDto>();
            var outp = new List<Look>(dtos.Count);
            foreach (var d in dtos) outp.Add(d.ToLook());
            return outp;
        }
        catch { return new List<Look>(); }
    }

    /// <summary>Save a look, replacing any existing one of the same name (case-insensitive).</summary>
    public void Save(Look look)
    {
        if (look is null || string.IsNullOrWhiteSpace(look.Name)) return;
        var all = Load();
        all.RemoveAll(l => string.Equals(l.Name, look.Name, StringComparison.OrdinalIgnoreCase));
        all.Add(look);
        Write(all);
    }

    /// <summary>Delete a saved look by name; returns true if one was removed.</summary>
    public bool Delete(string name)
    {
        var all = Load();
        int removed = all.RemoveAll(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Write(all);
        return removed > 0;
    }

    private void Write(List<Look> looks)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var dtos = new List<LookDto>(looks.Count);
            foreach (var l in looks) dtos.Add(LookDto.From(l));
            File.WriteAllText(_path, JsonSerializer.Serialize(dtos, s_json));
        }
        catch { /* best-effort persistence; a write failure must not crash the tool */ }
    }

    // Primitive, round-trippable projection of a Look (colours packed 0xAARRGGBB).
    private sealed class LookDto
    {
        public string Name { get; set; } = "";
        public List<uint> Ramp { get; set; } = new();
        public float Roughness { get; set; }
        public float Metallic { get; set; }
        public uint KeyTint { get; set; }
        public uint SkyTint { get; set; }
        public uint? EmissionTint { get; set; }

        public static LookDto From(Look l)
        {
            var ramp = new List<uint>(l.Ramp.Count);
            foreach (var (r, g, b) in l.Ramp) ramp.Add(Pack(r, g, b));
            return new LookDto
            {
                Name = l.Name,
                Ramp = ramp,
                Roughness = l.Material.Roughness,
                Metallic = l.Material.Metallic,
                KeyTint = Pack(l.Lighting.KeyTint),
                SkyTint = Pack(l.Lighting.SkyTint),
                EmissionTint = l.EmissionTint is { } e ? Pack(e) : (uint?)null,
            };
        }

        public Look ToLook()
        {
            var ramp = new List<(byte r, byte g, byte b)>(Ramp.Count);
            foreach (var u in Ramp) ramp.Add(Unpack(u));
            if (ramp.Count == 0) ramp.Add((128, 128, 128));   // never an empty ramp
            return new Look(
                Name,
                ramp,
                new LookMaterial(Roughness, Metallic),
                new LookLighting(Unpack(KeyTint), Unpack(SkyTint)),
                EmissionTint is { } e ? Unpack(e) : ((byte, byte, byte)?)null);
        }

        private static uint Pack(byte r, byte g, byte b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        private static uint Pack((byte r, byte g, byte b) c) => Pack(c.r, c.g, c.b);
        private static (byte, byte, byte) Unpack(uint u) => ((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF));
    }
}
