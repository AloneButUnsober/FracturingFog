// Views/FFClientView.axaml.cs
// Code-behind for FFClientView. Hooks the BrowseFileRequested + SaveBytesRequested
// events into Avalonia's StorageProvider, then writes the user's chosen path back
// onto the VM via the supplied callback.

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public partial class FFClientView : Window
{
    public FFClientView() { InitializeComponent(); DataContextChanged += OnDcChanged; }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDcChanged(object? sender, EventArgs e)
    {
        if (DataContext is FFClientViewModel vm)
        {
            vm.BrowseFileRequested  += async (_, t) => await BrowseAsync(t.kind, t.suggestedName, t.assign);
            vm.SaveBytesRequested   += async (_, args) => await SaveBytesAsync(args);
            // Use Close() so MainWindow.SyncFFClient's Closing handler
            // intercepts, cancels the close, and flips the shell flag false
            // — which lets the next ShowFFClient transition reopen. Calling
            // Hide() directly here would skip the flag flip and the dialog
            // would never reappear on the second click.
            vm.CloseRequested       += (_, _) => Close();
        }
    }

    private async Task BrowseAsync(string kind, string? suggestedName, Action<string> assign)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        if (kind == "output")
        {
            var save = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save rendered output as…",
                SuggestedFileName = !string.IsNullOrEmpty(suggestedName) ? suggestedName : "render.png",
            });
            if (save != null) assign(save.Path.LocalPath);
            return;
        }

        var open = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = kind == "clientCert" ? "Pick client .pfx" : "Pick server CA .pfx",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PFX (PKCS#12)") { Patterns = new[] { "*.pfx", "*.p12" } },
                FilePickerFileTypes.All,
            },
        });
        if (open is { Count: > 0 }) assign(open[0].Path.LocalPath);
    }

    private async Task SaveBytesAsync(SaveBytesEventArgs args)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) { args.Completion.SetResult(); return; }
            var save = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save server response…",
                SuggestedFileName = !string.IsNullOrEmpty(args.SuggestedName)
                    ? args.SuggestedName
                    : "ff-render." + args.DefaultExtension,
            });
            if (save == null) { args.Completion.SetResult(); return; }
            string path = save.Path.LocalPath;
            await File.WriteAllBytesAsync(path, args.Bytes);
            args.WrittenPath = path;
            args.Completion.SetResult();
        }
        catch (Exception ex)
        {
            args.WrittenPath = null;
            args.Completion.SetException(ex);
        }
    }
}
