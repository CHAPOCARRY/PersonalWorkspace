using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.Views;

// Native file access is possible only inside AssetService's validated, Profile-gated callback.
public sealed class AssetContentView : UserControl
{
    private readonly Asset asset;
    private readonly bool image;
    private readonly Func<IAssetFileConsumer, Task> useFile;
    private readonly Action<string> error;
    private readonly Image picture = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Button open = new() { Content = "Open" };
    private bool loaded;
    private int generation;
    public bool CanOpen { set => open.IsEnabled = value; }
    public AssetContentView(Asset asset, WidgetAssetSettings settings, bool image, Func<IAssetFileConsumer, Task> useFile, Action<string> error)
    {
        this.asset = asset; this.image = image; this.useFile = useFile; this.error = error;
        if (image)
        {
            Height = 180; picture.Stretch = settings.Fit == ImageFitMode.Fill ? Stretch.UniformToFill : Stretch.Uniform;
            AutomationProperties.SetName(picture, string.IsNullOrWhiteSpace(settings.AltText) ? asset.OriginalFileName : settings.AltText);
            AutomationProperties.SetAutomationId(picture, "WidgetImage");
            Content = picture;
            SizeChanged += (_, _) => Clip = new RectangleGeometry { Rect = new(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)) };
            Loaded += async (_, _) => await LoadImage();
            Unloaded += (_, _) => { generation++; loaded = false; picture.Source = null; };
        }
        else
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new FontIcon { Glyph = "\uE8A5", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 24 });
            panel.Children.Add(new TextBlock { Text = asset.MediaType + " · " + Size(asset.ByteLength), TextWrapping = TextWrapping.Wrap });
            AutomationProperties.SetAutomationId(open, "AttachmentOpen"); AutomationProperties.SetName(open, "Open " + asset.OriginalFileName);
            open.Click += async (_, _) =>
            {
                open.IsEnabled = false;
                try { await useFile(new FileAction(async (path, _) =>
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    if (!await Windows.System.Launcher.LaunchFileAsync(file)) throw new ContentValidationException("Windows could not open this file. Check its default application.");
                })); }
                catch (Exception e) { error(e is ContentValidationException ? e.Message : "Attachment file unavailable or could not be opened."); }
                finally { open.IsEnabled = true; }
            };
            panel.Children.Add(open); Content = panel;
        }
    }
    public static string Size(long bytes) => bytes < 1024 ? bytes.ToString("N0") + " B" : bytes < 1024 * 1024 ? (bytes / 1024m).ToString("0.#") + " KB" : (bytes / (1024m * 1024)).ToString("0.#") + " MB";
    private async Task LoadImage()
    {
        if (!image || loaded) return; loaded = true; var request = ++generation;
        try
        {
            BitmapImage? bitmap = null;
            await useFile(new FileAction(async (path, _) =>
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                bitmap = new BitmapImage();
                if (asset.PixelWidth >= asset.PixelHeight) bitmap.DecodePixelWidth = Math.Min(1600, asset.PixelWidth ?? 1600);
                else bitmap.DecodePixelHeight = Math.Min(1600, asset.PixelHeight ?? 1600);
                await bitmap.SetSourceAsync(stream);
            }));
            if (request == generation) { picture.Source = bitmap; Content = picture; }
        }
        catch (Exception)
        {
            if (request == generation)
                Content = new TextBlock
                {
                    Text = "Image file unavailable or could not be decoded. Remove the Widget or restore the file.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["Danger"]
                };
        }
    }
    private sealed class FileAction(Func<string, Asset, Task> action) : IAssetFileConsumer
    {
        public Task UseAsync(string validatedPath, Asset asset, CancellationToken token) { token.ThrowIfCancellationRequested(); return action(validatedPath, asset); }
    }
}
