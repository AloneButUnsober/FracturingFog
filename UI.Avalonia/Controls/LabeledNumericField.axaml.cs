// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Controls
{
    /// <summary>
    /// Label + <see cref="NumericUpDown"/> composite with a shared, generous
    /// default field width so numeric values are never clipped. Drop-in
    /// replacement for the ad-hoc label+NumericUpDown pairs scattered across
    /// dialogs. All display/behavior knobs are StyledProperties so it binds
    /// like any control.
    /// </summary>
    public partial class LabeledNumericField : UserControl
    {
        public static readonly StyledProperty<string> LabelProperty =
            AvaloniaProperty.Register<LabeledNumericField, string>(nameof(Label), string.Empty);

        public static readonly StyledProperty<decimal?> ValueProperty =
            AvaloniaProperty.Register<LabeledNumericField, decimal?>(
                nameof(Value), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

        public static readonly StyledProperty<decimal> MinimumProperty =
            AvaloniaProperty.Register<LabeledNumericField, decimal>(nameof(Minimum), decimal.MinValue);

        public static readonly StyledProperty<decimal> MaximumProperty =
            AvaloniaProperty.Register<LabeledNumericField, decimal>(nameof(Maximum), decimal.MaxValue);

        public static readonly StyledProperty<decimal> IncrementProperty =
            AvaloniaProperty.Register<LabeledNumericField, decimal>(nameof(Increment), 1m);

        public static readonly StyledProperty<string> FormatStringProperty =
            AvaloniaProperty.Register<LabeledNumericField, string>(nameof(FormatString), "0.###");

        public static readonly StyledProperty<string> UnitProperty =
            AvaloniaProperty.Register<LabeledNumericField, string>(nameof(Unit), string.Empty);

        public static readonly StyledProperty<double> LabelWidthProperty =
            AvaloniaProperty.Register<LabeledNumericField, double>(nameof(LabelWidth), 96);

        public static readonly StyledProperty<double> FieldWidthProperty =
            AvaloniaProperty.Register<LabeledNumericField, double>(nameof(FieldWidth), 120);

        public static readonly StyledProperty<bool> HasUnitProperty =
            AvaloniaProperty.Register<LabeledNumericField, bool>(nameof(HasUnit));

        public LabeledNumericField()
        {
            InitializeComponent();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        public string Label
        {
            get => GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public decimal? Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public decimal Minimum
        {
            get => GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public decimal Maximum
        {
            get => GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public decimal Increment
        {
            get => GetValue(IncrementProperty);
            set => SetValue(IncrementProperty, value);
        }

        public string FormatString
        {
            get => GetValue(FormatStringProperty);
            set => SetValue(FormatStringProperty, value);
        }

        public string Unit
        {
            get => GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        public double LabelWidth
        {
            get => GetValue(LabelWidthProperty);
            set => SetValue(LabelWidthProperty, value);
        }

        public double FieldWidth
        {
            get => GetValue(FieldWidthProperty);
            set => SetValue(FieldWidthProperty, value);
        }

        public bool HasUnit
        {
            get => GetValue(HasUnitProperty);
            set => SetValue(HasUnitProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            // Keep HasUnit in sync so the trailing unit label only occupies
            // space when a unit string is present.
            if (change.Property == UnitProperty)
                HasUnit = !string.IsNullOrEmpty(Unit);
        }
    }
}
