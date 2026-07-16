// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Program.cs — ColorGen CLI entry.
//
// Mirrors CalculatorGen.Program — minimal command-line driver so the
// library is also exercisable as `ColorGen <source-path> <ClassName>
// [<ThemeDisplayName>]`. Reads the DSL source from a file, renders the
// generated C# class, writes it to stdout (or to --out <path>).

using System;
using System.IO;
using FracturingFog.ColorGen;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: ColorGen <source-path> <ClassName> [--name <ThemeDisplayName>] [--out <out.cs>]");
    return 2;
}

string srcPath  = args[0];
string clsName  = args[1];
string? themeName = null;
string? outPath = null;
for (int i = 2; i < args.Length - 1; i++)
{
    if (args[i] == "--name") themeName = args[++i];
    else if (args[i] == "--out") outPath = args[++i];
}

if (!File.Exists(srcPath))
{
    Console.Error.WriteLine($"Source file not found: {srcPath}");
    return 3;
}

string source = File.ReadAllText(srcPath);
var opts = new GenerateOptions { ThemeName = themeName ?? clsName };
var result = ColorGenApi.Generate(source, clsName, opts);

if (!result.Ok)
{
    Console.Error.WriteLine(result.Error);
    return 1;
}

if (outPath != null)
{
    File.WriteAllText(outPath, result.Source);
    Console.Error.WriteLine($"Wrote {outPath}");
}
else
{
    Console.Write(result.Source);
}
return 0;
