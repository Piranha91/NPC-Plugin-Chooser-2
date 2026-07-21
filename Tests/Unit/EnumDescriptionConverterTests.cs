using System.Globalization;
using FluentAssertions;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="EnumDescriptionConverter"/> is a display-only WPF value converter. It must
/// return an enum's [Description] (or its name as a fallback) and, critically, must never
/// throw — a hard cast that failed would crash the whole window during layout. WPF can
/// momentarily activate the binding against a non-enum value while a ComboBox selection
/// box's ItemTemplate is instantiating (observed opening the Favorite Faces window on
/// net10), so the non-enum path is exercised here explicitly.
/// </summary>
public class EnumDescriptionConverterTests
{
    private readonly EnumDescriptionConverter _converter = new();

    private object? Convert(object? value) =>
        _converter.Convert(value!, typeof(string), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_EnumWithDescription_ReturnsDescription()
    {
        Convert(FavoriteFaceSearchType.EditorID).Should().Be("EditorID");
        Convert(NpcSearchType.InAppearanceMod).Should().Be("In Appearance Mod");
    }

    [Fact]
    public void Convert_EnumWithoutDescription_FallsBackToName()
    {
        Convert(GenderFilterType.Female).Should().Be("Female");
        Convert(UniquenessFilterType.Generic).Should().Be("Generic");
    }

    [Fact]
    public void Convert_Null_ReturnsEmptyString()
    {
        Convert(null).Should().Be(string.Empty);
    }

    [Fact]
    public void Convert_NonEnumString_DoesNotThrowAndReturnsString()
    {
        var act = () => Convert("All Favorites");
        act.Should().NotThrow("a display converter must not crash the UI on a stray non-enum value");
        Convert("All Favorites").Should().Be("All Favorites");
    }
}
