using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Window for selecting and managing keywords for a mod setting.
/// </summary>
public partial class KeywordSelectionWindow : Window, INotifyPropertyChanged
{
    private ObservableCollection<string> _currentKeywords = new();
    private ObservableCollection<string> _otherKeywords = new();
    private ObservableCollection<string> _filteredOtherKeywords = new();
    private HashSet<string> _originalOtherKeywords = new();

    /// <summary>
    /// Gets or sets the name of the mod being edited.
    /// </summary>
    public string ModName { get; set; } = string.Empty;

    /// <summary>
    /// Gets the current keywords for this mod after editing.
    /// </summary>
    public ObservableCollection<string> CurrentKeywords
    {
        get => _currentKeywords;
        private set
        {
            _currentKeywords = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets whether any changes were made.
    /// </summary>
    public bool HasChanged { get; private set; } = false;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public KeywordSelectionWindow()
    {
        InitializeComponent();
        CurrentKeywordsListBox.ItemsSource = _currentKeywords;
        OtherKeywordsListBox.ItemsSource = _filteredOtherKeywords;

        // Handle Enter key in the new keyword textbox
        NewKeywordTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddNewKeyword();
                e.Handled = true;
            }
        };
    }

    /// <summary>
    /// Initializes the window with the current mod's keywords and keywords from other mods.
    /// </summary>
    /// <param name="modName">The name of the mod being edited.</param>
    /// <param name="currentKeywords">The current keywords for this mod.</param>
    /// <param name="otherKeywords">Keywords from all other mods (will be made unique).</param>
    public void Initialize(string modName, IEnumerable<string> currentKeywords, IEnumerable<string> otherKeywords)
    {
        ModName = modName;
        ModNameTextBlock.Text = modName;

        _currentKeywords.Clear();
        foreach (var keyword in currentKeywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            _currentKeywords.Add(keyword);
        }

        // Store unique keywords from other mods, excluding ones already in current
        _originalOtherKeywords = new HashSet<string>(otherKeywords, StringComparer.OrdinalIgnoreCase);
        
        RefreshOtherKeywordsList();
        UpdateStatus();
    }

    private void RefreshOtherKeywordsList()
    {
        var currentKeywordsSet = new HashSet<string>(_currentKeywords, StringComparer.OrdinalIgnoreCase);
        var filterText = FilterTextBox.Text?.Trim() ?? string.Empty;

        _filteredOtherKeywords.Clear();
        
        var filtered = _originalOtherKeywords
            .Where(k => !currentKeywordsSet.Contains(k))
            .Where(k => string.IsNullOrEmpty(filterText) || 
                       k.Contains(filterText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var keyword in filtered)
        {
            _filteredOtherKeywords.Add(keyword);
        }
    }

    private void UpdateStatus()
    {
        StatusTextBlock.Text = $"{_currentKeywords.Count} keyword(s) assigned. " +
                              $"{_filteredOtherKeywords.Count} keyword(s) available from other mods.";
    }

    private void AddNewKeyword()
    {
        var newKeyword = NewKeywordTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newKeyword))
        {
            return;
        }

        // Check if keyword already exists (case-insensitive)
        if (_currentKeywords.Any(k => k.Equals(newKeyword, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"The keyword '{newKeyword}' already exists.",
                "Duplicate Keyword",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _currentKeywords.Add(newKeyword);
        SortCurrentKeywords();
        NewKeywordTextBox.Clear();
        HasChanged = true;
        RefreshOtherKeywordsList();
        UpdateStatus();
    }

    private void SortCurrentKeywords()
    {
        var sorted = _currentKeywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        _currentKeywords.Clear();
        foreach (var keyword in sorted)
        {
            _currentKeywords.Add(keyword);
        }
    }

    private void AddKeywordFromOthers(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return;
        }

        // Check if keyword already exists (case-insensitive)
        if (_currentKeywords.Any(k => k.Equals(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _currentKeywords.Add(keyword);
        SortCurrentKeywords();
        HasChanged = true;
        RefreshOtherKeywordsList();
        UpdateStatus();
    }

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentKeywordsListBox.SelectedItem is string selectedKeyword)
        {
            _currentKeywords.Remove(selectedKeyword);
            HasChanged = true;
            RefreshOtherKeywordsList();
            UpdateStatus();
        }
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentKeywords.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show("Are you sure you want to remove all keywords from this mod?",
            "Clear All Keywords",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _currentKeywords.Clear();
            HasChanged = true;
            RefreshOtherKeywordsList();
            UpdateStatus();
        }
    }

    private void AddNewKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        AddNewKeyword();
    }

    private void AddFromOthersButton_Click(object sender, RoutedEventArgs e)
    {
        if (OtherKeywordsListBox.SelectedItem is string selectedKeyword)
        {
            AddKeywordFromOthers(selectedKeyword);
        }
    }

    private void OtherKeywordsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OtherKeywordsListBox.SelectedItem is string selectedKeyword)
        {
            AddKeywordFromOthers(selectedKeyword);
        }
    }

    private void FilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RefreshOtherKeywordsList();
        UpdateStatus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        HasChanged = false;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Gets the final list of keywords after the dialog closes.
    /// </summary>
    public HashSet<string> GetKeywords()
    {
        return new HashSet<string>(_currentKeywords, StringComparer.OrdinalIgnoreCase);
    }
}