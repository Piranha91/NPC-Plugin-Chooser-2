using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FluentAssertions;
using NPC_Plugin_Chooser_2.Views;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

// Exercises the actual modal dialog without a game environment or user input.
[Collection(NpcChooserIntegrationCollection.Name)]
public class ImportChoiceDialogTests
{
    private const string DialogTitle = "Confirm Import";

    private readonly WpfStaFixture _sta;
    public ImportChoiceDialogTests(WpfStaFixture sta) => _sta = sta;

    [Theory]
    [InlineData("keep", true)]
    [InlineData("clear", false)]
    [InlineData("cancel", null)]
    [InlineData("close", null)]
    public async Task Dialog_MapsEachChoiceAndDefaultsToKeep(string action, bool? expected)
    {
        await _sta.RunOnStaAsync(() =>
        {
            Exception? callbackError = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                // Find this test's dialog by title: another ScrollableMessageBox
                // (e.g. a stray warning) may also be open on this Application.
                // The lookup sits inside the try so a miss fails the test
                // instead of leaving the modal open and hanging the run.
                try
                {
                    var box = Application.Current.Windows.OfType<ScrollableMessageBox>()
                        .Single(w => w.Title == DialogTitle);
                    var panel = (StackPanel)box.FindName("ButtonPanel");
                    var buttons = panel.Children.OfType<Button>().Where(b => b.IsVisible).ToArray();
                    buttons.Select(b => b.Content).Should().Equal("Import & Keep Others", "Import & Clear Others", "Cancel");
                    buttons[0].IsDefault.Should().BeTrue();
                    buttons[2].IsCancel.Should().BeTrue();
                    panel.ActualWidth.Should().BeLessThanOrEqualTo(box.ActualWidth - 20);
                    if (action != "close")
                    {
                        int index = action == "keep" ? 0 : action == "clear" ? 1 : 2;
                        buttons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                }
                catch (Exception ex)
                {
                    callbackError = ex;
                }
                finally
                {
                    foreach (var open in Application.Current.Windows.OfType<ScrollableMessageBox>()
                                 .Where(w => w.Title == DialogTitle && w.IsVisible).ToList())
                    {
                        open.Close();
                    }
                }
            }));

            var result = ScrollableMessageBox.ChooseWithCancel(
                "Import 2 choices? You have choices for 3 NPCs not listed in this file.", DialogTitle,
                "Import & Keep Others", "Import & Clear Others");

            if (callbackError != null) throw callbackError;
            result.Should().Be(expected);
        });
    }
}
