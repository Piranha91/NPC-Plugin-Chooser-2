using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    
    public ScrollableMessageBox(string message,
        string title = "Message",
        MessageBoxImage messageBoxImage = MessageBoxImage.None,
        string displayImagePath = "",
        ImagePosition displayImagePos = ImagePosition.Left,
        double displayImageSize = 150,
        bool isConfirmation = false)
    {
        InitializeComponent();
        Title = title;
        MessageTextBox.Text = message;
        _isConfirmation = isConfirmation;

        // System icon
        this.Icon = SystemIconsFromMessageBoxImage(messageBoxImage);

        // Optional image
        if (!string.IsNullOrEmpty(displayImagePath) && File.Exists(displayImagePath))
        {
            DisplayImage.Source = new BitmapImage(new Uri(displayImagePath));
            DisplayImage.Visibility = Visibility.Visible;
            SetDockPosition(displayImagePos);
            ResizeImageDynamically(displayImageSize);
        }

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
        double displayImageSize = 150)
    {
        new ScrollableMessageBox(message, title, messageBoxImage, displayImagePath, displayImagePos, displayImageSize)
            .ShowDialog();
    }

    public static void ShowWarning(string message,
        string title = "Warning",
        string displayImagePath = "",
        ImagePosition displayImagePos = ImagePosition.Left,
        double displayImageSize = 150)
    {
        Show(message, title, MessageBoxImage.Warning, displayImagePath, displayImagePos, displayImageSize);
    }

    public static void ShowError(string message,
        string title = "Error",
        string displayImagePath = "",
        ImagePosition displayImagePos = ImagePosition.Left,
        double displayImageSize = 150)
    {
        Show(message, title, MessageBoxImage.Error, displayImagePath, displayImagePos, displayImageSize);
    }

    
    public static bool Confirm(string message,
        string title = "Confirm",
        MessageBoxImage messageBoxImage = MessageBoxImage.Question,
        string displayImagePath = "",
        ImagePosition displayImagePos = ImagePosition.Left,
        double displayImageSize = 150)
    {
        var box = new ScrollableMessageBox(message, title, messageBoxImage, displayImagePath, displayImagePos, displayImageSize, isConfirmation: true);
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

    private void ResizeImageDynamically(double baseSize)
    {
        DisplayImage.Width = baseSize;
        DisplayImage.Height = baseSize;

        SizeChanged += (s, e) =>
        {
            double scaleFactor = ActualHeight / 600.0;
            DisplayImage.Width = DisplayImage.Height = baseSize * scaleFactor;
        };
    }

    private void AdjustWindowSizeToContent()
    {
        MessageTextBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double desiredHeight = MessageTextBox.DesiredSize.Height + 150;

        double maxHeight = SystemParameters.WorkArea.Height - 100;
        Height = Math.Min(desiredHeight, maxHeight);
    }

}