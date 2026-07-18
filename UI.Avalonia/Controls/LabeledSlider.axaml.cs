// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Controls
{
    /// <summary>
    /// Label + <see cref="Slider"/> + numeric readout. Canonical control for
    /// bounded continuous parameters so they render consistently everywhere
    /// (the fix for "brightness is a slider in one dialog, a numeric field in
    /// another"). <see cref="ValueText"/> is a derived readout formatted via
    /// <see cref="FormatString"/> plus an optional <see cref="Unit"/> suffix.
    /// </summary>
    public partial class LabeledSlider : UserControl
    {
        public static readonly StyledProperty<string> LabelProperty =
            AvaloniaProperty.Register<LabeledSlider, string>(nameof(Label), string.Empty);

        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<LabeledSlider, double>(
                nameof(Value), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

        public static readonly StyledProperty<double> MinimumProperty =
            AvaloniaProperty.Register<LabeledSlider, double>(nameof(Minimum), 0d);

        public static readonly StyledProperty<double> MaximumProperty =
            AvaloniaProperty.Register<LabeledSlider, double>(nameof(Maximum), 100d);

        public static readonly StyledProperty<double> TickFrequencyProperty =
            AvaloniaProperty.Register<LabeledSlider, double>(nameof(TickFrequency), 0d);

        public static readonly StyledProperty<string> FormatStringProperty =
            AvaloniaProperty.Register<LabeledSlider, string>(nameof(FormatString), "0");

        public static readonly StyledProperty<string> UnitProperty =
            AvaloniaProperty.Register<LabeledSlider, string>(nameof(Unit), string.Empty);

        public static readonly StyledProperty<double> LabelWidthProperty =
            AvaloniaProperty.Register<LabeledSlider, double>(nameof(LabelWidth), 96);

        public static readonly DirectProperty<LabeledSlider, string> ValueTextProperty =
            AvaloniaProperty.RegisterDirect<LabeledSlider, string>(
                nameof(ValueText), o => o.ValueText);

        private string _valueText = string.Empty;

        public LabeledSlider()
        {
            InitializeComponent();
            UpdateValueText();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        public string Label
        {
            get => GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public double Minimum
        {
            get => GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public double TickFrequency
        {
            get => GetValue(TickFrequencyProperty);
            set => SetValue(TickFrequencyProperty, value);
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

        /// <summary>Formatted, read-only value readout shown at the row's tail.</summary>
        public string ValueText
        {
            get => _valueText;
            private set => SetAndRaise(ValueTextProperty, ref _valueText, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ValueProperty
                || change.Property == FormatStringProperty
                || change.Property == UnitProperty)
            {
                UpdateValueText();
            }
        }

        private void UpdateValueText()
        {
            string fmt = string.IsNullOrEmpty(FormatString) ? "0" : FormatString;
            string text = Value.ToString(fmt, CultureInfo.CurrentCulture);
            ValueText = string.IsNullOrEmpty(Unit) ? text : text + Unit;
        }
    }
}
