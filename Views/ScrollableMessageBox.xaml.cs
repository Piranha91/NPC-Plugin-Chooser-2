using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Interaction logic for ScrollableMessageBox.xaml
/// </summary>

public enum ImagePosition
{
    Left,
    Top,
    Right,
    Bottom
}


public partial class ScrollableMessageBox : Window
{
    private bool? _dialogResult = null;
    private bool _isConfirmation = false;
    private const int DefaultWindowWidth = 500;
    private const int MinWindowHeight = 500;
    private const double DefaultImageScalingFactor = 0.25;
    private double _naturalAspect = double.NaN;
    
    public ScrollableMessageBox(string message,
        string title = "Message",
        MessageBoxImage messageBoxImage = MessageBoxImage.None,
        string displayImagePath = "",
        ImagePosition displayImagePos = ImagePosition.Left,
        double? displayImageWidthRatio  = double.NaN,   // ⇦ changed
        double? displayImageHeightRatio = double.NaN,   // ⇦ changed
        bool isConfirmation = false)
    {
        InitializeComponent();
        Title = title;
        MessageTextBox.Text = message;
        _isConfirmation = isConfirmation;
        
        // Set the initial dimensions from the constant (height is controlled dynamically)
        this.Width = DefaultWindowWidth;

        // System icon
        this.Icon = SystemIconsFromMessageBoxImage(messageBoxImage);

        // --- Robust Optional Image Loading ---
        if (!string.IsNullOrWhiteSpace(displayImagePath))
        {
            Debug.WriteLine($"ScrollableMessageBox: Loading image: {displayImagePath}");
            Uri? imageUri = null;
            try
            {
                // Determine if the path is absolute (e.g., C:\...) or relative
                if (Path.IsPathFullyQualified(displayImagePath))
                {
                    // Absolute path: Check existence and create file URI
                    if (File.Exists(displayImagePath))
                    {
                        imageUri = new Uri(displayImagePath, UriKind.Absolute);
                        Debug.WriteLine($"ScrollableMessageBox: Using absolute file URI: {imageUri}");
                    }
                    else
                    {
                        Debug.WriteLine($"ScrollableMessageBox: Absolute image path not found: {displayImagePath}");
                    }
                }
                else
                {
                    // Relative path: Assume it's relative to application content, create Pack URI
                    // Ensure forward slashes for URI format
                    string relativePathForUri = displayImagePath.Replace('\\', '/');
                    // Ensure it doesn't start with a slash if it's already relative
                    if (relativePathForUri.StartsWith("/")) 
                    {
                        relativePathForUri = relativePathForUri.Substring(1);
                    }
                    imageUri = new Uri($"pack://application:,,,/{relativePathForUri}", UriKind.Absolute);
                    Debug.WriteLine($"ScrollableMessageBox: Using Pack URI: {imageUri}");
                    // Note: We don't explicitly check File.Exists here for pack URIs,
                    // as the resource should be resolved from the application package.
                    // BitmapImage constructor will throw if the resource isn't found.
                }

                // If we successfully created a URI, attempt to load the image
                if (imageUri != null)
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = imageUri;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // Load image data immediately
                    bitmap.EndInit();

                    DisplayImage.Source = bitmap;
                    DisplayImage.Visibility = Visibility.Visible;
                    SetDockPosition(displayImagePos);
                    InitializeDynamicResize(
                        DisplayImage,                       // the <Image> control to resize
                        displayImageWidthRatio  ?? double.NaN,
                        displayImageHeightRatio ?? double.NaN);

                    Debug.WriteLine($"ScrollableMessageBox: Successfully loaded image source for URI: {imageUri.OriginalString}");
                }
                else
                {
                    Debug.WriteLine($"ScrollableMessageBox: Could not load image source for URI: {displayImagePath}");
                }
            }
            catch (UriFormatException uriEx)
            {
                 Debug.WriteLine($"Error creating URI for display image '{displayImagePath}': {uriEx.Message}");
                 DisplayImage.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) // Catch other potential errors (IO, loading)
            {
                Debug.WriteLine($"Error loading display image '{displayImagePath}' (URI attempted: {imageUri?.OriginalString ?? "N/A"}): {ex.Message}");
                DisplayImage.Visibility = Visibility.Collapsed; // Hide image control on error
            }
        }
        // --- End Robust Image Loading ---

        // Button setup
        if (_isConfirmation)
        {
            YesButton.Visibility = Visibility.Visible;
            NoButton.Visibility = Visibility.Visible;
        }
        else
        {
            OkButton.Visibility = Visibility.Visible;
        }

        Loaded += (s, e) => AdjustWindowSizeToContent();
    }
    
    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        _dialogResult = true;
        Close();
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        _dialogResult = true;
        Close();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        _dialogResult = false;
        Close();
    }

    public static void Show(string message,
        string title = "Message",
        MessageBoxImage messageBoxImage = MessageBoxImage.None,
        string displayImagePath = "",
        ImagePosition displayImagePos = ImagePosition.Left,
        double? displayImageWidthRatio = double.NaN,
        double? displayImageHeightRatio = double.NaN)
    {
        new ScrollableMessageBox(message, title, messageBoxImage, displayImagePath, displayImagePos, displayImageWidthRatio, displayImageHeightRatio)
            .ShowDialog();
    }

    public static void ShowWarning(string message,
        string title = "Warning",
        string displayImagePath = "",
        ImagePosition displayImagePos = ImagePosition.Left,
        double? displayImageWidthRatio = double.NaN,
        double? displayImageHeightRatio = double.NaN)
    {
        Show(message, title, MessageBoxImage.Warning, displayImagePath, displayImagePos, displayImageWidthRatio, displayImageHeightRatio);
    }

    public static void ShowError(string message,
        string title = "Error",
        string displayImagePath = "",
        ImagePosition displayImagePos = ImagePosition.Left,
        double? displayImageWidthRatio = double.NaN,
        double? displayImageHeightRatio = double.NaN)
    {
        Show(message, title, MessageBoxImage.Error, displayImagePath, displayImagePos, displayImageWidthRatio: displayImageWidthRatio, displayImageHeightRatio);
    }
    
    public static bool Confirm(string message,
        string title = "Confirm",
        MessageBoxImage messageBoxImage = MessageBoxImage.Question,
        string displayImagePath = "",
        ImagePosition displayImagePos = ImagePosition.Left,
        double? displayImageWidthRatio = double.NaN,
        double? displayImageHeightRatio = double.NaN)
    {
        var box = new ScrollableMessageBox(message, title, messageBoxImage, displayImagePath, displayImagePos, displayImageWidthRatio, displayImageHeightRatio, isConfirmation: true);
        box.ShowDialog();
        return box._dialogResult == true;
    }

    
    private ImageSource SystemIconsFromMessageBoxImage(MessageBoxImage image)
    {
        return image switch
        {
            MessageBoxImage.Information => SystemIcons.Information.ToImageSource(),
            MessageBoxImage.Warning => SystemIcons.Warning.ToImageSource(),
            MessageBoxImage.Error => SystemIcons.Error.ToImageSource(),
            MessageBoxImage.Question => SystemIcons.Question.ToImageSource(),
            _ => null
        };
    }

    private void SetDockPosition(ImagePosition position)
    {
        DockPanel.SetDock(DisplayImage, position switch
        {
            ImagePosition.Top => Dock.Top,
            ImagePosition.Right => Dock.Right,
            ImagePosition.Bottom => Dock.Bottom,
            _ => Dock.Left
        });
    }

    /// Dynamically resizes an <Image> so it always uses the requested fraction
    /// of *either* the parent’s width or height, preserving aspect ratio.
    ///
    /// widthRatio  – fraction of parent width to occupy   (NaN = unused)
    /// heightRatio – fraction of parent height to occupy  (NaN = unused)
    private void InitializeDynamicResize(
        Image  img,
        double widthRatio  = double.NaN,
        double heightRatio = double.NaN)
    {
        // ---------------------------------------------------------------
        // 0. Decide which dimension drives the scaling
        // ---------------------------------------------------------------
        bool   scaleByHeight   = !double.IsNaN(heightRatio) && heightRatio > 0;
        double scalingFactor   = scaleByHeight
                                 ? heightRatio
                                 : !double.IsNaN(widthRatio) && widthRatio > 0
                                       ? widthRatio
                                       : DefaultImageScalingFactor;

        // ---------------------------------------------------------------
        // 1. Cache the natural aspect ratio ONCE
        // ---------------------------------------------------------------
        if (double.IsNaN(_naturalAspect))
        {
            if (img.Source is BitmapSource bmp && bmp.PixelWidth > 0)
            {
                _naturalAspect = bmp.PixelHeight / (double)bmp.PixelWidth;
            }
            else
            {
                // Image not ready yet → re‑run when it loads
                img.Loaded += (_, __) => InitializeDynamicResize(img, widthRatio, heightRatio);
                return;
            }
        }
        double aspect = _naturalAspect;

        // ---------------------------------------------------------------
        // 2. Attach ONE handler to the host (parent) element
        // ---------------------------------------------------------------
        FrameworkElement host = img.Parent as FrameworkElement ?? img;

        SizeChangedEventHandler hostHandler = null;
        hostHandler = (_, __) =>
        {
            double hostW = host.ActualWidth;
            double hostH = host.ActualHeight;

            // If the host is still 0×0 (first layout), fall back to bitmap pixels
            if (hostW <= 0) hostW = img.Source is BitmapSource b ? b.PixelWidth  : 0;
            if (hostH <= 0) hostH = img.Source is BitmapSource b ? b.PixelHeight : 0;

            if (scaleByHeight)
            {
                img.Height = hostH * scalingFactor;
                img.Width  = img.Height / aspect;
            }
            else
            {
                img.Width  = hostW * scalingFactor;
                img.Height = img.Width * aspect;
            }
        };

        // Make sure we never stack duplicates
        host.SizeChanged -= hostHandler;
        host.SizeChanged += hostHandler;

        // ---------------------------------------------------------------
        // 3. Kick off the first resize *after* layout completes
        // ---------------------------------------------------------------
        host.Dispatcher.BeginInvoke(
            hostHandler,
            System.Windows.Threading.DispatcherPriority.Loaded,
            null, null);
    }


    private void AdjustWindowSizeToContent()
    {
        MessageTextBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double desiredHeight = MessageTextBox.DesiredSize.Height + 150;

        double maxHeight = SystemParameters.WorkArea.Height - 100;
        Height = Math.Min(desiredHeight, maxHeight);

        if (DisplayImage.Visibility == Visibility.Visible && Height < DisplayImage.ActualHeight)
        {
            Height = DisplayImage.ActualHeight;
        }
    }

}