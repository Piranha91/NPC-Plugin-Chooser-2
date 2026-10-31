using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// GitKraken-style connector lines for the NPCs tab's grouped list (template / same-name tree).
/// Each row draws only its own slice: the ancestor lanes passing through it, its elbow into its
/// parent's lane, and — when expanded — the start of its own lane down to its children. Slices of
/// adjacent rows meet up because each one stretches over its ListBoxItem's padding.
///
/// <para>Lane k sits at the centre of the k-th <see cref="LaneWidth"/> column, which is where a
/// depth-k row's expand arrow is, and takes colour k of the palette.</para>
/// </summary>
public sealed class NpcTreeLines : FrameworkElement
{
    public static readonly DependencyProperty GuidesProperty = DependencyProperty.Register(
        nameof(Guides), typeof(IReadOnlyList<bool>), typeof(NpcTreeLines),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HasChildrenProperty = DependencyProperty.Register(
        nameof(HasChildren), typeof(bool), typeof(NpcTreeLines),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(NpcTreeLines),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    // The row template's outer Border insets the content (selection-bar mode). Lanes are drawn
    // back out by that much so they stay straight past the selected row.
    public static readonly DependencyProperty RowInsetProperty = DependencyProperty.Register(
        nameof(RowInset), typeof(Thickness), typeof(NpcTreeLines),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RowBorderProperty = DependencyProperty.Register(
        nameof(RowBorder), typeof(Thickness), typeof(NpcTreeLines),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<bool>? Guides
    {
        get => (IReadOnlyList<bool>?)GetValue(GuidesProperty);
        set => SetValue(GuidesProperty, value);
    }

    public bool HasChildren
    {
        get => (bool)GetValue(HasChildrenProperty);
        set => SetValue(HasChildrenProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public Thickness RowInset
    {
        get => (Thickness)GetValue(RowInsetProperty);
        set => SetValue(RowInsetProperty, value);
    }

    public Thickness RowBorder
    {
        get => (Thickness)GetValue(RowBorderProperty);
        set => SetValue(RowBorderProperty, value);
    }

    public const double LaneWidth = 14;
    private const double LineThickness = 2;
    private const double CornerRadius = 4;
    private const double DotRadius = 3;
    private const double ArrowHalfHeight = 6;

    // Mid-saturation hues that read on both the light and dark themes.
    private static readonly Pen[] Pens = CreatePens(
        "#4FA3E0", "#B065D8", "#E0A030", "#3FB27F", "#E05A6A", "#2BB5B5");

    private static Pen[] CreatePens(params string[] hex)
    {
        var pens = new Pen[hex.Length];
        for (int i = 0; i < hex.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex[i]));
            brush.Freeze();
            var pen = new Pen(brush, LineThickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            pen.Freeze();
            pens[i] = pen;
        }
        return pens;
    }

    private static Pen PenFor(int lane) => Pens[lane % Pens.Length];

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        var guides = Guides;
        int depth = guides?.Count ?? 0;
        bool ownLane = HasChildren && IsExpanded;
        if (depth == 0 && !ownLane) return;

        double shift = RowInset.Left + RowBorder.Left;
        double cy = Math.Round(ActualHeight / 2);
        var (top, bottom) = VerticalExtent();
        double LaneX(int lane) => lane * LaneWidth + LaneWidth / 2 - shift;

        // Ancestor lanes passing straight through this row.
        for (int k = 0; k < depth - 1; k++)
        {
            if (!guides![k]) continue;
            dc.DrawLine(PenFor(k), new Point(LaneX(k), top), new Point(LaneX(k), bottom));
        }

        if (depth > 0)
        {
            // Elbow from the parent's lane into this row, rounded like a git graph branch-off.
            int lane = depth - 1;
            var pen = PenFor(lane);
            double x = LaneX(lane);
            double ownX = depth * LaneWidth + LaneWidth / 2; // this row's arrow / dot (not shifted)
            double endX = HasChildren ? ownX - 5 : ownX;

            if (guides![lane]) dc.DrawLine(pen, new Point(x, top), new Point(x, bottom));

            var elbow = new StreamGeometry();
            using (var ctx = elbow.Open())
            {
                ctx.BeginFigure(new Point(x, top), false, false);
                ctx.LineTo(new Point(x, cy - CornerRadius), true, false);
                ctx.ArcTo(new Point(x + CornerRadius, cy), new Size(CornerRadius, CornerRadius), 0,
                    false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new Point(Math.Max(endX, x + CornerRadius), cy), true, false);
            }
            elbow.Freeze();
            dc.DrawGeometry(null, pen, elbow);

            if (!HasChildren) dc.DrawEllipse(pen.Brush, null, new Point(ownX, cy), DotRadius, DotRadius);
        }

        if (ownLane)
        {
            double x = LaneX(depth);
            dc.DrawLine(PenFor(depth), new Point(x, cy + ArrowHalfHeight), new Point(x, bottom));
        }
    }

    /// <summary>
    /// Top and bottom of the hosting ListBoxItem in local coordinates, so a lane spans the item's
    /// padding and joins the next row. Falls back to this element's own bounds.
    /// </summary>
    private (double Top, double Bottom) VerticalExtent()
    {
        DependencyObject? cur = this;
        while (cur != null && cur is not ListBoxItem) cur = VisualTreeHelper.GetParent(cur);
        if (cur is not ListBoxItem item || !item.IsAncestorOf(this)) return (0, ActualHeight);

        double offset = TranslatePoint(new Point(0, 0), item).Y;
        return (-offset, item.ActualHeight - offset);
    }
}
