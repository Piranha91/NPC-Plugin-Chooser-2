using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Windows;
using System.Windows.Input; // For MouseButtonEventArgs

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for FullScreenImageView.xaml
    /// </summary>
    public partial class FullScreenImageView : ReactiveWindow<VM_FullScreenImage>
    {
        public FullScreenImageView()
        {
            InitializeComponent();
            this.WhenActivated(d =>
            {
                // ViewModel should be set by the caller (VM_AppearanceMod)
                this.OneWayBind(ViewModel, vm => vm.ImagePath, v => v.FullScreenImage.Source).DisposeWith(d);
                // You might want to bind MaxWidth/MaxHeight for very large images,
                // though Stretch="Uniform" handles most cases visually.
            });
        }

        // Close window on click anywhere
        private void CloseOnClick(object sender, MouseButtonEventArgs e)
        {
            this.Close();
        }
    }
}
// Add x:Name="FullScreenImage" to the Image tag if binding from code-behind