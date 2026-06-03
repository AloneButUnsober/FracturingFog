// Models/RecentFiles.cs
//
// Tiny MRU list persisted as JSON. The MainWindow appends a path on every
// successful image load and reads the list to populate the Recent submenu.

using System.Collections.Generic;

namespace PaletteBuilder.Models;

public sealed class RecentFiles
{
    /// <summary>Most-recently-used first. Capped to <see cref="MaxItems"/>.</summary>
    public List<string> Paths { get; set; } = new();

    public const int MaxItems = 12;
}
