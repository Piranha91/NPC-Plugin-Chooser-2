using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// A selectable item representing a master plugin.
/// </summary>
public class SelectableMaster : INotifyPropertyChanged
{
    private bool _isSelected;

    public ModKey ModKey { get; set; }
    public string FileName => ModKey.FileName;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Window for selecting which master plugins to analyze.
/// </summary>
public partial class MasterSelectionWindow : Window
{
    public ObservableCollection<SelectableMaster> Masters { get; } = new();
    
    /// <summary>
    /// Gets the list of selected masters after the dialog closes with a positive result.
    /// </summary>
    public List<ModKey> SelectedMasters => Masters
        .Where(m => m.IsSelected)
        .Select(m => m.ModKey)
        .ToList();

    /// <summary>
    /// Gets or sets the path to the target plugin being analyzed.
    /// </summary>
    public string TargetPluginPath { get; set; } = string.Empty;

    public MasterSelectionWindow()
    {
        InitializeComponent();
        MastersListBox.ItemsSource = Masters;
    }

    /// <summary>
    /// Initializes the window with the target plugin path and its masters.
    /// </summary>
    /// <param name="targetPluginPath">Full path to the plugin being analyzed.</param>
    /// <param name="masters">List of master ModKeys from the plugin header.</param>
    public void Initialize(string targetPluginPath, IEnumerable<ModKey> masters)
    {
        TargetPluginPath = targetPluginPath;
        TargetPluginTextBlock.Text = Path.GetFileName(targetPluginPath);

        Masters.Clear();
        foreach (var master in masters)
        {
            Masters.Add(new SelectableMaster
            {
                ModKey = master,
                IsSelected = true // Default to all selected
            });
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int selectedCount = Masters.Count(m => m.IsSelected);
        StatusTextBlock.Text = $"{selectedCount} of {Masters.Count} master(s) selected for analysis.";
        AnalyzeButton.IsEnabled = selectedCount > 0;
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var master in Masters)
        {
            master.IsSelected = true;
        }
        UpdateStatus();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var master in Masters)
        {
            master.IsSelected = false;
        }
        UpdateStatus();
    }

    private void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SelectedMasters.Any())
        {
            MessageBox.Show("Please select at least one master to analyze.", 
                "No Masters Selected", 
                MessageBoxButton.OK, 
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        
        // Subscribe to IsSelected changes for status updates
        foreach (var master in Masters)
        {
            master.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(SelectableMaster.IsSelected))
                {
                    UpdateStatus();
                }
            };
        }
    }
}