// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>#1434 — audio recipe picker (see <c>AudioRecipePickerViewModel</c>).</summary>
public sealed partial class AudioRecipePickerView : UserControl
{
    public AudioRecipePickerView() => AvaloniaXamlLoader.Load(this);
}
