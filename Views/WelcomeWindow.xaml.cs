using System.Windows;

namespace NPC_Plugin_Chooser_2.Views;

public enum WelcomeChoice
{
    Manual,
    Wizard,
    ImportSettings,
}

/// <summary>Shown once, on a launch with no Settings.json (see <c>App.OfferSetupWizardIfFreshInstall</c>).</summary>
public partial class WelcomeWindow : Window
{
    public WelcomeChoice Choice { get; private set; } = WelcomeChoice.Manual;

    public WelcomeWindow()
    {
        InitializeComponent();
    }

    private void Wizard_Click(object sender, RoutedEventArgs e) => Close(WelcomeChoice.Wizard);
    private void Manual_Click(object sender, RoutedEventArgs e) => Close(WelcomeChoice.Manual);
    private void Import_Click(object sender, RoutedEventArgs e) => Close(WelcomeChoice.ImportSettings);

    private void Close(WelcomeChoice choice)
    {
        Choice = choice;
        Close();
    }
}
