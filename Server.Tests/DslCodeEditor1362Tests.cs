// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using AvaloniaEdit.Document;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.Controls;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #1362 (S1 of #1360) — DslCodeEditor's non-visual pieces: the code-view
/// prefs store, and the minimal document replace that keeps the caret and a
/// single undo step when the VM pushes text into the code view.
/// </summary>
public sealed class DslCodeEditor1362Tests
{
    [Fact]
    public void Prefs_store_round_trips_per_key()
    {
        // Data root is redirected to a temp dir for the whole test process
        // (TestDataRootIsolation), so this reads/writes the throwaway file.
        var prefs = new DslEditorPrefs();
        prefs.CodeView["UserBulb"] = true;
        prefs.CodeView["Sandbox"] = false;
        DslEditorPrefsStore.Save(prefs);

        var loaded = DslEditorPrefsStore.Load();
        Assert.True(loaded.CodeView["UserBulb"]);
        Assert.False(loaded.CodeView["Sandbox"]);
        Assert.False(loaded.CodeView.ContainsKey("UserEquation"));
    }

    [Theory]
    [InlineData("z*z + c", "z*z*z + c")]          // insert in the middle
    [InlineData("z*z*z + c", "z*z + c")]          // delete from the middle
    [InlineData("abs(z) + c", "norm(z) + c")]     // Apply-fix style token swap
    [InlineData("", "z^2 + c")]                   // empty -> text
    [InlineData("z^2 + c", "")]                   // text -> empty
    [InlineData("aaaa", "aa")]                    // repeated chars: prefix/suffix must not overlap
    [InlineData("aa", "aaaa")]
    [InlineData("z + c", "z + c")]                // unchanged
    public void ReplaceMinimal_produces_exact_text(string before, string after)
    {
        var doc = new TextDocument(before);
        DslCodeEditor.ReplaceMinimal(doc, after);
        Assert.Equal(after, doc.Text);
    }

    [Fact]
    public void ReplaceMinimal_keeps_offsets_before_the_edit_and_is_one_undo_step()
    {
        var doc = new TextDocument("let r = abs(z) in r*r + c");
        // An anchor stands in for the caret: AvaloniaEdit moves the caret with
        // document changes exactly as it moves anchors.
        var before = doc.CreateAnchor(4);              // inside "r", before the fix
        var after = doc.CreateAnchor(doc.TextLength);  // end of text

        DslCodeEditor.ReplaceMinimal(doc, "let r = norm(z) in r*r + c");

        Assert.Equal("let r = norm(z) in r*r + c", doc.Text);
        Assert.Equal(4, before.Offset);                // untouched prefix: caret stays put
        Assert.Equal(doc.TextLength, after.Offset);    // suffix shifted by the edit, still at end

        Assert.True(doc.UndoStack.CanUndo);
        doc.UndoStack.Undo();
        Assert.Equal("let r = abs(z) in r*r + c", doc.Text);
        Assert.False(doc.UndoStack.CanUndo);           // exactly one undo step
    }
}
