using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EDNexus.App.Controls;

/// <summary>
/// Lays children out in as many equal-width columns as the available width allows, dropping each
/// one into whichever column is shortest *when it first needs a column* and keeping it there after
/// that, so cards never trade places as their heights change.
/// </summary>
/// <remarks>
/// A <see cref="WrapPanel"/> would tie every item in a row to the height of the tallest one, which
/// leaves a band of dead space under the short items; a <see cref="Grid"/> ties them to a fixed
/// column count that only looks right at one window size. Here an item's height is its own, and the
/// column count follows the width, so the settings blocks keep packing tight as the window resizes.
/// </remarks>
public class MasonryPanel : Panel
{
    /// <summary>Narrowest a column may get before the panel drops to fewer columns.</summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(MinColumnWidth), 320);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(ColumnSpacing), 16);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(RowSpacing), 16);

    /// <summary>
    /// How many columns a child claims. Anything wider than the current column count is clamped, so
    /// a two-column item still shows up whole on a window only wide enough for one.
    /// </summary>
    public static readonly AttachedProperty<int> ColumnSpanProperty =
        AvaloniaProperty.RegisterAttached<MasonryPanel, Control, int>("ColumnSpan", 1);

    /// <summary>
    /// The column a child has been placed in by hand. <see cref="AutoColumn"/> — the default —
    /// leaves it to the packer. Clamped to the columns that currently fit, so a narrow window
    /// folds a hand-made arrangement together instead of dropping cards off the side.
    /// </summary>
    public static readonly AttachedProperty<int> ColumnProperty =
        AvaloniaProperty.RegisterAttached<MasonryPanel, Control, int>("Column", AutoColumn);

    public const int AutoColumn = -1;

    public static int GetColumnSpan(Control control) => control.GetValue(ColumnSpanProperty);

    public static void SetColumnSpan(Control control, int value) => control.SetValue(ColumnSpanProperty, value);

    public static int GetColumn(Control control) => control.GetValue(ColumnProperty);

    public static void SetColumn(Control control, int value) => control.SetValue(ColumnProperty, value);

    static MasonryPanel()
    {
        AffectsMeasure<MasonryPanel>(MinColumnWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
        AffectsParentMeasure<MasonryPanel>(ColumnSpanProperty, ColumnProperty);
    }

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    // The column the packer chose for each auto-placed child, remembered per column count. Without it
    // the "shortest column" choice is re-made on every layout pass, so a card whose height changes
    // (a status line, a goal's progress) tips the balance and two cards trade places on their own.
    // A choice is made once, when a child first needs one at a given column count, and kept after
    // that; it is dropped only when the child itself goes away. Keyed by the child's data context
    // (the card), not the container, so it survives the containers being recreated.
    private readonly Dictionary<int, Dictionary<object, int>> _stableColumns = [];

    private static object StableKey(Control child) => child.DataContext ?? child;

    // Where each child landed, by index: first column, offset down it, and how many columns it
    // claimed. Column -1 means the child is collapsed and takes no space.
    private (int Column, double Top, int Span)[] _placements = [];
    private double[] _columnHeights = [];
    private double _columnWidth;

    /// <summary>The column a child was actually drawn in, which for an auto-placed one the packer chose.</summary>
    public int RenderedColumn(Control child)
    {
        var index = Children.IndexOf(child);
        return index < 0 || index >= _placements.Length ? AutoColumn : _placements[index].Column;
    }

    /// <summary>
    /// Resolve a point to a drop target: the column under it, and the child the dragged card would
    /// be inserted above — null when it belongs at the bottom of that column.
    /// </summary>
    public (int Column, Control? Before) DropTargetAt(Point point)
    {
        if (_columnHeights.Length == 0 || _columnWidth <= 0) return (0, null);

        var column = Math.Clamp(
            (int)(point.X / (_columnWidth + ColumnSpacing)), 0, _columnHeights.Length - 1);

        for (var i = 0; i < Children.Count; i++)
        {
            var (childColumn, _, span) = _placements[i];
            if (childColumn < 0) continue;
            if (column < childColumn || column >= childColumn + span) continue;

            // Above the midpoint of the first card it meets going down the column: insert here.
            if (point.Y <= Children[i].Bounds.Center.Y) return (column, Children[i]);
        }

        return (column, null);
    }

    /// <summary>
    /// The bar marking where a drop would land, in this panel's coordinates, so the caller drawing
    /// it does not have to know the column geometry.
    /// </summary>
    public Rect IndicatorFor(int column, Control? before)
    {
        var left = column * (_columnWidth + ColumnSpacing);
        var top = before is not null
            ? before.Bounds.Top
            : _columnHeights.Length > 0 ? _columnHeights[Math.Clamp(column, 0, _columnHeights.Length - 1)] : 0;

        // Inset to line up with the card bodies, which carry an 8px margin of their own.
        return new Rect(left + 8, Math.Max(0, top - 2), Math.Max(0, _columnWidth - 16), 4);
    }

    protected override Size MeasureOverride(Size availableSize) => Pack(availableSize.Width);

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The final width can differ from the one measured against, and column width — hence every
        // child's height — is a function of it, so pack once more against what we actually got.
        Pack(finalSize.Width);

        for (var i = 0; i < Children.Count; i++)
        {
            var (column, top, span) = _placements[i];
            if (column < 0) continue;

            var left = column * (_columnWidth + ColumnSpacing);
            Children[i].Arrange(new Rect(left, top, SpanWidth(span), Children[i].DesiredSize.Height));
        }

        return finalSize;
    }

    /// <summary>
    /// Measures every child at the column width and assigns it to the shortest column, returning
    /// the size the packed result needs.
    /// </summary>
    private Size Pack(double availableWidth)
    {
        var visible = 0;
        foreach (var child in Children)
        {
            if (child.IsVisible) visible++;
        }

        var columns = ColumnCount(availableWidth, visible);
        _columnWidth = ColumnWidth(availableWidth, columns);
        _columnHeights = new double[columns];
        _placements = new (int, double, int)[Children.Count];

        if (!_stableColumns.TryGetValue(columns, out var stable))
            _stableColumns[columns] = stable = [];
        ForgetRemovedChildren();

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (!child.IsVisible)
            {
                _placements[i] = (-1, 0, 0);
                continue;
            }

            var span = Math.Clamp(GetColumnSpan(child), 1, columns);
            child.Measure(new Size(SpanWidth(span), double.PositiveInfinity));

            var assigned = GetColumn(child);
            int column;
            if (assigned >= 0)
            {
                column = Math.Clamp(assigned, 0, columns - span);
            }
            else if (stable.TryGetValue(StableKey(child), out var kept) && kept + span <= columns)
            {
                column = kept;                // placed before: stay put whatever the heights are now
            }
            else
            {
                column = BestColumn(span);    // first time at this column count: shortest column
                stable[StableKey(child)] = column;
            }

            var top = TopOf(column, span);
            if (top > 0) top += RowSpacing;   // no leading gap at the top of a column

            _placements[i] = (column, top, span);

            // A spanning child sets the floor for every column it covers, so nothing slides
            // underneath it.
            for (var c = column; c < column + span; c++)
            {
                _columnHeights[c] = top + child.DesiredSize.Height;
            }
        }

        var width = columns * _columnWidth + (columns - 1) * ColumnSpacing;
        var height = 0d;
        foreach (var columnHeight in _columnHeights)
        {
            if (columnHeight > height) height = columnHeight;
        }

        return new Size(width, height);
    }

    /// <summary>
    /// Drops remembered columns for children no longer in the panel. A child that is merely hidden
    /// keeps its entry, so showing it again puts it back where it was.
    /// </summary>
    private void ForgetRemovedChildren()
    {
        var present = new HashSet<object>(Children.Count);
        foreach (var child in Children) present.Add(StableKey(child));

        foreach (var perCount in _stableColumns.Values)
        {
            foreach (var key in perCount.Keys.Where(k => !present.Contains(k)).ToList())
                perCount.Remove(key);
        }
    }

    private int ColumnCount(double availableWidth, int visibleChildren)
    {
        if (visibleChildren == 0) return 1;

        // Unconstrained width (an auto-sizing or horizontally scrolling parent) has no column count
        // to derive, so fall back to a single column of the minimum width.
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth) || MinColumnWidth <= 0)
            return 1;

        var fit = (int)Math.Floor((availableWidth + ColumnSpacing) / (MinColumnWidth + ColumnSpacing));

        // Never more columns than there are items — an empty column is the whitespace this panel
        // exists to avoid, and the leftovers are better spent widening the items that do exist.
        return Math.Clamp(fit, 1, visibleChildren);
    }

    private double ColumnWidth(double availableWidth, int columns)
    {
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth)) return MinColumnWidth;

        return Math.Max(0, (availableWidth - (columns - 1) * ColumnSpacing) / columns);
    }

    private double SpanWidth(int span) => span * _columnWidth + (span - 1) * ColumnSpacing;

    /// <summary>
    /// The leftmost start column at which a run of <paramref name="span"/> columns sits highest —
    /// for the common span of 1 that is simply the shortest column. Ties go left, so items keep
    /// roughly the reading order they were declared in.
    /// </summary>
    private int BestColumn(int span)
    {
        var best = 0;
        var bestTop = double.MaxValue;

        for (var start = 0; start + span <= _columnHeights.Length; start++)
        {
            var top = TopOf(start, span);
            if (top < bestTop)
            {
                bestTop = top;
                best = start;
            }
        }

        return best;
    }

    /// <summary>Where a child starting at <paramref name="column"/> has to clear every column it covers.</summary>
    private double TopOf(int column, int span)
    {
        var top = 0d;
        for (var c = column; c < column + span && c < _columnHeights.Length; c++)
        {
            if (_columnHeights[c] > top) top = _columnHeights[c];
        }

        return top;
    }
}
