using EDNexus.Core.Colonisation;
using Xunit;

namespace EDNexus.Tests.Colonisation;

/// <summary>
/// Noticing a second tool reporting the same carrier cargo. Raven adds up whatever it is told, so a
/// duplicate reporter shows up as the carrier moving by more than EDNexus alone reported.
/// </summary>
public class CarrierDriftMonitorTests
{
    private static Dictionary<string, int> Map(params (string name, int units)[] items)
        => items.ToDictionary(i => i.name, i => i.units);

    private static CarrierDriftMonitor WithBaseline(params (string name, int units)[] items)
    {
        var monitor = new CarrierDriftMonitor();
        monitor.SetBaseline(Map(items));
        return monitor;
    }

    [Fact]
    public void Replies_that_match_what_was_sent_are_never_drift()
    {
        var monitor = WithBaseline(("steel", 1000));

        Assert.False(monitor.Observe(Map(("steel", 100)), Map(("steel", 1100))).IsDrift);
        Assert.False(monitor.Observe(Map(("steel", -250)), Map(("steel", 850))).IsDrift);
        Assert.False(monitor.Observe(Map(("titanium", 40)), Map(("steel", 850), ("titanium", 40))).IsDrift);
    }

    [Fact]
    public void Without_a_baseline_the_first_reply_becomes_one_and_proves_nothing()
    {
        var monitor = new CarrierDriftMonitor();

        Assert.False(monitor.HasBaseline);
        Assert.False(monitor.Observe(Map(("steel", 100)), Map(("steel", 5000))).IsDrift);
        Assert.True(monitor.HasBaseline);
        Assert.False(monitor.Observe(Map(("steel", 100)), Map(("steel", 5100))).IsDrift);
    }

    [Fact]
    public void A_baseline_is_only_taken_once()
    {
        var monitor = WithBaseline(("steel", 1000));
        monitor.SetBaseline(Map(("steel", 9)));          // a later lookup must not rebase and mask drift

        Assert.False(monitor.Observe(Map(("steel", 100)), Map(("steel", 1100))).IsDrift);
    }

    [Fact]
    public void The_server_moving_by_twice_what_was_sent_is_a_second_reporter()
    {
        var monitor = WithBaseline(("steel", 1000));

        var verdict = monitor.Observe(Map(("steel", 100)), Map(("steel", 1200)));   // +100 from us, +100 from them

        Assert.True(verdict.IsDrift);
        Assert.Contains("steel", verdict.Detail);
    }

    [Fact]
    public void A_duplicate_that_lands_after_our_reply_is_caught_on_the_next_send()
    {
        var monitor = WithBaseline(("steel", 1000));
        Assert.False(monitor.Observe(Map(("steel", 100)), Map(("steel", 1100))).IsDrift);   // theirs not in yet

        // Their +100 has landed since; this send is about something else entirely.
        var verdict = monitor.Observe(Map(("titanium", 40)), Map(("steel", 1200), ("titanium", 40)));

        Assert.True(verdict.IsDrift);
    }

    [Fact]
    public void A_removal_that_is_duplicated_is_caught_too()
    {
        var monitor = WithBaseline(("steel", 1000));

        Assert.True(monitor.Observe(Map(("steel", -300)), Map(("steel", 400))).IsDrift);
    }

    [Fact]
    public void One_unexplained_change_is_tolerated_but_two_in_a_row_are_not()
    {
        var monitor = WithBaseline(("steel", 1000));

        // A squadmate edited the manifest on the website: not a duplicate of anything we sent.
        Assert.False(monitor.Observe(Map(("steel", 10)), Map(("steel", 1017))).IsDrift);
        Assert.True(monitor.Observe(Map(("steel", 10)), Map(("steel", 1031))).IsDrift);
    }

    [Fact]
    public void A_clean_send_between_two_odd_ones_resets_the_count()
    {
        var monitor = WithBaseline(("steel", 1000));

        Assert.False(monitor.Observe(Map(("steel", 10)), Map(("steel", 1017))).IsDrift);
        Assert.False(monitor.Observe(Map(("steel", 10)), Map(("steel", 1027))).IsDrift);   // exactly as expected
        Assert.False(monitor.Observe(Map(("steel", 10)), Map(("steel", 1044))).IsDrift);
    }

    [Fact]
    public void A_removal_larger_than_the_stock_is_not_judged_because_the_server_may_clamp()
    {
        var monitor = WithBaseline(("steel", 50));

        Assert.False(monitor.Observe(Map(("steel", -80)), Map()).IsDrift);
        Assert.False(monitor.Observe(Map(("steel", 20)), Map(("steel", 20))).IsDrift);
    }

    [Fact]
    public void A_commodity_that_empties_may_be_missing_from_the_reply()
    {
        var monitor = WithBaseline(("steel", 100), ("gold", 5));

        // Raven omits zero entries: steel is gone, so the reply lists only what is left.
        Assert.False(monitor.Observe(Map(("steel", -100)), Map(("gold", 5))).IsDrift);
        Assert.False(monitor.Observe(Map(("steel", 30)), Map(("steel", 30))).IsDrift);
    }

    [Fact]
    public void A_reply_with_no_totals_proves_nothing_but_the_monitor_keeps_tracking()
    {
        var monitor = WithBaseline(("steel", 1000));

        Assert.False(monitor.Observe(Map(("steel", 100)), Map()).IsDrift);           // body was empty
        Assert.False(monitor.Observe(Map(("steel", 100)), Map(("steel", 1200))).IsDrift);   // 1000 + 100 + 100 as sent
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var monitor = WithBaseline(("steel", 1000));
        monitor.Reset();

        Assert.False(monitor.HasBaseline);
        Assert.False(monitor.Observe(Map(("steel", 100)), Map(("steel", 7777))).IsDrift);
    }
}
