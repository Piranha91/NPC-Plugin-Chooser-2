using System.Windows;
using System.Windows.Controls;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Reports a zero desired size, then arranges its child into whatever space the
/// parent grants. Lets a control (e.g. the Lock 3D View toggle's icon) fill the
/// height its row already has without contributing to that height — an Image
/// measured against an Auto row would otherwise ask for its full pixel size.
/// </summary>
public class ZeroDesiredSizeDecorator : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(constraint);
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
