using Avalonia;
using Avalonia.Controls;
using EDNexus.App.Controls;
using Xunit;

namespace EDNexus.App.Tests;

/// <summary>
/// The dashboard must never rearrange itself (#180): a card that was put in a column stays there while
/// card heights change underneath it. The panel is laid out directly, without a window.
/// </summary>
public class MasonryPanelTests
{
    private const double Wide = 220;   // two 100px-minimum columns fit
    private const double Narrow = 150; // only one fits

    private static (MasonryPanel Panel, List<Border> Cards) Build(params double[] heights)
    {
        var panel = new MasonryPanel { MinColumnWidth = 100, ColumnSpacing = 0, RowSpacing = 0 };
        var cards = new List<Border>();
        foreach (var height in heights)
        {
            // The DataContext stands in for the card view model the real dashboard binds.
            var card = new Border { Height = height, DataContext = new object() };
            cards.Add(card);
            panel.Children.Add(card);
        }

        return (panel, cards);
    }

    private static void Layout(MasonryPanel panel, double width)
    {
        panel.InvalidateMeasure();
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
    }

    private static int[] Columns(MasonryPanel panel, List<Border> cards)
        => cards.Select(panel.RenderedColumn).ToArray();

    [Fact]
    public void Cards_stay_in_their_column_when_their_heights_change()
    {
        // A and B fill one column each, then C joins the (tied) left column.
        var (panel, cards) = Build(100, 100, 50);
        Layout(panel, Wide);
        var before = Columns(panel, cards);
        Assert.Equal([0, 1, 0], before);

        // A grows a lot: the shortest column is now the right one. Re-deciding would move C there.
        cards[0].Height = 300;
        Layout(panel, Wide);

        Assert.Equal(before, Columns(panel, cards));
    }

    [Fact]
    public void Many_height_changes_never_move_a_card()
    {
        var (panel, cards) = Build(80, 120, 60, 90, 70, 110);
        Layout(panel, Wide);
        var before = Columns(panel, cards);
        var random = new Random(180);

        for (var round = 0; round < 50; round++)
        {
            foreach (var card in cards) card.Height = 20 + random.Next(300);
            Layout(panel, Wide);
            Assert.Equal(before, Columns(panel, cards));
        }
    }

    [Fact]
    public void Narrowing_then_widening_restores_the_arrangement()
    {
        var (panel, cards) = Build(100, 100, 50, 70);
        Layout(panel, Wide);
        var wide = Columns(panel, cards);

        Layout(panel, Narrow);
        Assert.All(Columns(panel, cards), column => Assert.Equal(0, column));

        cards[0].Height = 400; // content changes while the window is narrow
        Layout(panel, Wide);

        Assert.Equal(wide, Columns(panel, cards));
    }

    [Fact]
    public void A_hidden_card_returns_to_the_column_it_left()
    {
        var (panel, cards) = Build(100, 100, 50, 70);
        Layout(panel, Wide);
        var before = Columns(panel, cards);

        cards[2].IsVisible = false;
        Layout(panel, Wide);
        cards[0].Height = 300;
        cards[2].IsVisible = true;
        Layout(panel, Wide);

        Assert.Equal(before, Columns(panel, cards));
    }

    [Fact]
    public void A_new_card_is_placed_without_moving_the_others()
    {
        var (panel, cards) = Build(100, 100, 50);
        Layout(panel, Wide);
        var before = Columns(panel, cards);

        var added = new Border { Height = 40, DataContext = new object() };
        panel.Children.Add(added);
        cards[0].Height = 250;
        Layout(panel, Wide);

        Assert.Equal(before, Columns(panel, cards));
        Assert.InRange(panel.RenderedColumn(added), 0, 1);
    }

    [Fact]
    public void A_removed_card_does_not_move_the_remaining_ones()
    {
        var (panel, cards) = Build(100, 100, 50, 70);
        Layout(panel, Wide);
        var before = Columns(panel, cards);

        panel.Children.Remove(cards[3]);
        Layout(panel, Wide);

        Assert.Equal(before.Take(3), Columns(panel, cards.Take(3).ToList()));
    }

    [Fact]
    public void A_card_keeps_its_column_when_its_container_is_recreated()
    {
        // The dashboard rebuilds the item containers when cards are rearranged; the card (the data
        // context) is what must keep its place.
        var (panel, cards) = Build(100, 100, 50);
        Layout(panel, Wide);
        var before = Columns(panel, cards);

        var recreated = cards.Select(c => new Border { Height = c.Height, DataContext = c.DataContext }).ToList();
        panel.Children.Clear();
        foreach (var card in recreated) panel.Children.Add(card);
        recreated[0].Height = 300;
        Layout(panel, Wide);

        Assert.Equal(before, Columns(panel, recreated));
    }

    [Fact]
    public void A_hand_placed_column_is_respected()
    {
        var (panel, cards) = Build(100, 100, 50);
        MasonryPanel.SetColumn(cards[2], 1);
        Layout(panel, Wide);

        Assert.Equal(1, panel.RenderedColumn(cards[2]));
    }
}
