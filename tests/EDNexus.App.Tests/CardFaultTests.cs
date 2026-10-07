using EDNexus.App.ViewModels;
using EDNexus.Core.State;

namespace EDNexus.App.Tests;

public sealed class CardFaultTests
{
    // The context is never touched by these stub cards, so none of its delegates are supplied.
    private static DashboardContext NoContext() => new(
        null!, () => false, new Random(1), null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private sealed class StubCard : CardViewModel
    {
        public bool Throw { get; set; }
        public int Updates { get; private set; }

        public StubCard() : base(NoContext(), "stub", "STUB", 452) { }

        public override void Update(CommanderState state)
        {
            Updates++;
            if (Throw) throw new InvalidOperationException("odd state");
        }
    }

    private sealed class FatalCard : CardViewModel
    {
        public FatalCard() : base(NoContext(), "fatal", "FATAL", 452) { }

        public override void Update(CommanderState state) => throw new OutOfMemoryException();
    }

    [Fact]
    public void AThrowingUpdate_IsContained_AndReported()
    {
        var card = new StubCard { Throw = true };
        var seen = new List<(int Failures, bool Paused)>();

        var ok = card.TryUpdate(new CommanderState(), (_, _, failures, paused) => seen.Add((failures, paused)));

        Assert.False(ok);
        Assert.Single(seen);
        Assert.Equal((1, false), seen[0]);
        Assert.False(card.IsFaulted);
        Assert.Equal("STUB", card.Title);
    }

    [Fact]
    public void RepeatedFailures_PauseTheCard_AndSurfaceItInTheTitle()
    {
        var card = new StubCard { Throw = true };
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        for (var i = 0; i < CardViewModel.MaxConsecutiveFailures; i++)
            card.TryUpdate(new CommanderState());

        Assert.True(card.IsFaulted);
        Assert.Equal("odd state", card.FaultMessage);
        Assert.Contains("paused", card.Title);
        Assert.Contains(nameof(CardViewModel.Title), raised);

        // A paused card is skipped rather than retried every tick.
        var before = card.Updates;
        Assert.True(card.TryUpdate(new CommanderState()));
        Assert.Equal(before, card.Updates);
    }

    [Fact]
    public void ASuccessfulUpdate_ResetsTheFailureStreak()
    {
        var card = new StubCard { Throw = true };
        for (var i = 0; i < CardViewModel.MaxConsecutiveFailures - 1; i++)
            card.TryUpdate(new CommanderState());

        card.Throw = false;
        Assert.True(card.TryUpdate(new CommanderState()));

        // Failures after the recovery start counting from zero again.
        card.Throw = true;
        for (var i = 0; i < CardViewModel.MaxConsecutiveFailures - 1; i++)
            card.TryUpdate(new CommanderState());
        Assert.False(card.IsFaulted);
    }

    [Fact]
    public void ClearFault_LetsAPausedCardUpdateAgain()
    {
        var card = new StubCard { Throw = true };
        for (var i = 0; i < CardViewModel.MaxConsecutiveFailures; i++)
            card.TryUpdate(new CommanderState());
        Assert.True(card.IsFaulted);

        card.ClearFault();
        card.Throw = false;
        var before = card.Updates;

        Assert.True(card.TryUpdate(new CommanderState()));
        Assert.False(card.IsFaulted);
        Assert.Null(card.FaultMessage);
        Assert.Equal("STUB", card.Title);
        Assert.Equal(before + 1, card.Updates);
    }

    [Fact]
    public void FatalExceptions_ArePropagated()
    {
        var card = new FatalCard();
        Assert.Throws<OutOfMemoryException>(() => card.TryUpdate(new CommanderState()));
    }
}
