using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetScope.Desktop.ViewModels;
using NetScope.Desktop.Views;

namespace NetScope.Desktop.Services;

/// <summary>
/// Off-screen render of every page for visual review. It renders the same ShellView the app shows,
/// but never opens a window, never starts observation and never touches the network.
/// </summary>
public static class ScreenRenderer
{
    private static readonly string[] Slugs =
    {
        "home", "timeline", "diagnoses", "clients", "export", "settings", "help",
    };

    public static async Task RenderAsync(MainViewModel viewModel, string directory, int width = 1440, int height = 900)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Directory.CreateDirectory(directory);
        await viewModel.LoadDemoAsync().ConfigureAwait(true);
        await viewModel.Clients.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        await viewModel.Export.PreviewAsync().ConfigureAwait(true);

        var shell = new ShellView
        {
            DataContext = viewModel,
            Width = width,
            Height = height,
        };

        for (var index = 0; index < viewModel.Sections.Count; index++)
        {
            viewModel.SelectedSection = viewModel.Sections[index];
            shell.Measure(new Size(width, height));
            shell.Arrange(new Rect(0, 0, width, height));
            shell.UpdateLayout();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(shell);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var name = index < Slugs.Length ? Slugs[index] : "page-" + index;
            using var stream = File.Create(Path.Combine(directory, $"{index + 1:00}-{name}.png"));
            encoder.Save(stream);
        }
    }
}
