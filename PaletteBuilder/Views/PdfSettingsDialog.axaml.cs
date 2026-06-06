// Views/PdfSettingsDialog.axaml.cs
//
// Modal pre-export dialog for PDF-specific tuning. Returns a populated
// PdfExportOptions (or null on cancel) via ShowDialogAsync. Defaults
// preserve the legacy 2-column Letter portrait layout so a user who just
// hits Enter gets the previous output.

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using PaletteBuilder.Services;

namespace PaletteBuilder.Views;

public sealed partial class PdfSettingsDialog : Window
{
    private TaskCompletionSource<PdfExportOptions?>? _tcs;

    public PdfSettingsDialog()
    {
        AvaloniaXamlLoader.Load(this);

        var pageCombo = this.FindControl<ComboBox>("PageSizeCombo")!;
        var orientCombo = this.FindControl<ComboBox>("OrientationCombo")!;
        pageCombo.SelectedIndex = 0;
        orientCombo.SelectedIndex = 0;

        this.FindControl<Button>("OkBtn")!.Click += (_, _) => Accept();
        this.FindControl<Button>("CancelBtn")!.Click += (_, _) => Cancel();
        Closed += (_, _) => _tcs?.TrySetResult(null);
    }

    public async Task<PdfExportOptions?> ShowDialogAsync(Window owner)
    {
        _tcs = new TaskCompletionSource<PdfExportOptions?>();
        await ShowDialog(owner);
        return await _tcs.Task;
    }

    private void Accept()
    {
        var pageCombo = this.FindControl<ComboBox>("PageSizeCombo")!;
        var orientCombo = this.FindControl<ComboBox>("OrientationCombo")!;
        var cols = this.FindControl<NumericUpDown>("ColumnsSpinner")!;

        var opts = new PdfExportOptions
        {
            PageSize = pageCombo.SelectedIndex switch
            {
                1 => PdfPageSize.Legal,
                2 => PdfPageSize.Tabloid,
                3 => PdfPageSize.A4,
                4 => PdfPageSize.A3,
                _ => PdfPageSize.Letter,
            },
            Orientation = orientCombo.SelectedIndex == 1
                ? PdfOrientation.Landscape
                : PdfOrientation.Portrait,
            Columns = (int)(cols.Value ?? 2m),
            IncludeCoverPage = this.FindControl<CheckBox>("CoverChk")!.IsChecked ?? false,
            IncludeSourceThumbnail = this.FindControl<CheckBox>("ThumbChk")!.IsChecked ?? false,
            IncludeGradientStrip = this.FindControl<CheckBox>("GradientChk")!.IsChecked ?? false,
            IncludeSwatchMetadata = this.FindControl<CheckBox>("MetaChk")!.IsChecked ?? false,
            IncludeCvdRows = this.FindControl<CheckBox>("CvdChk")!.IsChecked ?? false,
            IncludeComparisonPage = this.FindControl<CheckBox>("CompareChk")!.IsChecked ?? false,
        };
        _tcs?.TrySetResult(opts);
        Close();
    }

    private void Cancel()
    {
        _tcs?.TrySetResult(null);
        Close();
    }
}
