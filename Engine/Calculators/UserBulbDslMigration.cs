// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserBulbDslMigration.cs
//
// #27 / #211 — one-time, on-startup conversion of saved user bulbs from the
// historical C# `Vec3`/`Quat` form to the safe SandboxBulbExpression DSL. The 3D
// analogue of UserEquationDslMigration. After Phase 3 the live bulb path runs on
// the interpreter only (no raw-C# Roslyn), so a saved bulb the DSL can represent
// must be persisted as DSL — otherwise it fails to parse and shows a syntax
// error with no render (which is exactly what a pre-migration `userbulbs.json`
// does today).
//
// Lives in Engine because translation + validation need UserBulbSourcePreprocessor
// and the SandboxBulbExpression / SandboxBulbChain parsers (Engine), which the
// UI-free Abstractions store layer does not reference. The store owns the file +
// backup + save + the Sandbox-compiler pin; this injects the translate-and-parse
// step. Entries with no DSL form (arbitrary imperative Quat/Vec3 bodies —
// `.ToVec3()`, `Quat.FromVec3`, brace `if/else`, etc.) are left untouched and stay
// editable, surfacing the DSL error on the live path.

using System.Collections.Generic;

using FracturingFog.Models;

namespace FracturingFog;

public static class UserBulbDslMigration
{
    // The live calculator compiles the DSL with the animation time slot `t`
    // (plus any named params) in scope; mirror that here so a body referencing
    // `t` validates. Named params are not carried at migration time — a bulb
    // referencing a custom param name simply fails validation and is left
    // editable, same conservative contract as the equation migration.
    private static readonly List<string> Extras = new() { "t" };

    /// <summary>Convert translatable saved C# bulbs in the store to DSL. Backup-
    /// guarded and idempotent (see
    /// <see cref="UserBulbStore.MigrateUserBulbsToDsl"/>). Returns the number of
    /// entries changed.</summary>
    public static int Run(UserBulbStore store)
    {
        if (store == null) return 0;
        return store.MigrateUserBulbsToDsl(TranslateBody, TranslateChain);
    }

    /// <summary>Translate a single Vec3/Quat body to DSL and confirm it parses on
    /// the safe interpreter. Returns the DSL text, or null when the body has no
    /// DSL form or the translated text still fails to parse.</summary>
    private static string? TranslateBody(string source)
    {
        string? dsl = UserBulbSourcePreprocessor.Preprocess(source);
        if (string.IsNullOrWhiteSpace(dsl)) return null;
        try
        {
            SandboxBulbExpression.Parse(dsl!, Extras);
            return dsl;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Translate every step of a chain and confirm the rewritten chain
    /// parses as a whole (a later step may reference an earlier step's output by
    /// name, so the steps must be validated together, not in isolation). Returns
    /// the rewritten steps, or null when any step has no DSL form or the chain
    /// fails to parse.</summary>
    private static List<UserBulbChainStep>? TranslateChain(List<UserBulbChainStep> steps)
    {
        if (steps == null || steps.Count == 0) return null;

        var translated = new List<UserBulbChainStep>(steps.Count);
        foreach (var st in steps)
        {
            if (string.IsNullOrWhiteSpace(st.Source)) return null;
            string? dsl = UserBulbSourcePreprocessor.Preprocess(st.Source);
            if (string.IsNullOrWhiteSpace(dsl)) return null;
            translated.Add(new UserBulbChainStep { OutputName = st.OutputName, Source = dsl! });
        }

        try
        {
            SandboxBulbChain.Parse(translated, Extras);
            return translated;
        }
        catch
        {
            return null;
        }
    }
}
