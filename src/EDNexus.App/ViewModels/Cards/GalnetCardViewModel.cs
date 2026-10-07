using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.Core.Dev;
using EDNexus.Core.News;
using EDNexus.Core.State;

namespace EDNexus.App.ViewModels;

/// <summary>
/// Galnet: the in-universe news feed as a one-line ticker that cycles the latest headlines, opening
/// into a separate reader window when a headline is worth reading. Backed by the live Galnet feed, or
/// an offline sample generator while developer mode is on.
/// </summary>
/// <remarks>
/// News is ambiance, not instrumentation, so it earns a single line on the dashboard rather than a
/// panel: it loads once in the background on first tick, and a failed fetch leaves a note on that one
/// line rather than disturbing anything else.
/// </remarks>
public sealed partial class GalnetCardViewModel : CardViewModel
{
    /// <summary>Dashboard ticks are 250 ms, so this rotates the headline every six seconds.</summary>
    private const int TicksPerHeadline = 24;

    private SampleNewsFeed? _sampleNews;
    private bool _loadRequested;
    private int _tickCount;
    private int _tickerIndex;

    // The open reader, so a second click focuses it rather than stacking windows.
    private Window? _reader;

    public GalnetCardViewModel(DashboardContext context) : base(context, "galnet", "GALNET", 452) { }

    /// <summary>
    /// No journal sample source feeds this card, so it has no 🎲 reshuffle — developer mode swaps the
    /// whole feed for the offline generator instead, and Refresh pulls a fresh set from it.
    /// </summary>
    public override bool CanRandomize => false;

    private INewsFeed Feed => Context.DevEnabled ? _sampleNews ??= new SampleNewsFeed(Context.Rng) : Context.Host.News;

    /// <summary>Every article currently loaded. The ticker shows one; the reader window lists them all.</summary>
    public ObservableCollection<NewsHeadline> Headlines { get; } = new();

    [ObservableProperty] private bool _newsBusy;
    [ObservableProperty] private string _newsStatus = "";

    /// <summary>How many loaded headlines have never been opened — shown as a badge on the ticker.</summary>
    [ObservableProperty] private int _unreadCount;

    /// <summary>Whether the unread badge should show at all — keeps the XAML binding a plain bool.</summary>
    public bool HasUnread => UnreadCount > 0;

    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(HasUnread));

    // --- Ticker (the dashboard card) ---

    /// <summary>The headline currently on the ticker line, or null before anything has loaded.</summary>
    [ObservableProperty] private NewsHeadline? _tickerHeadline;

    [ObservableProperty] private bool _hasTicker;

    /// <summary>Position through the feed, shown as "3 / 15" so the ticker does not feel arbitrary.</summary>
    [ObservableProperty] private string _tickerPosition = "";

    // --- Reader (the pop-out window) ---

    [ObservableProperty] private NewsHeadline? _selectedHeadline;
    [ObservableProperty] private bool _hasArticle;
    [ObservableProperty] private string _articleTitle = "";
    [ObservableProperty] private string _articleBody = "";
    [ObservableProperty] private string _articleDate = "";

    partial void OnSelectedHeadlineChanged(NewsHeadline? value)
    {
        HasArticle = value is not null;
        ArticleTitle = value?.Title ?? "";
        ArticleBody = value?.Body ?? "";
        ArticleDate = value?.Published is { } p ? p.ToLocalTime().ToString("d MMM yyyy") : "";

        // SelectedHeadline also gets set behind the scenes on every load (to hold the commander's
        // place), so only treat it as "read" while the reader window is actually up — that's the only
        // time a selection means the commander is looking at it.
        if (value is not null && _reader is not null) MarkRead(value);
    }

    /// <summary>Mark one headline read: flips its badge off and persists it so it stays read across restarts.</summary>
    private void MarkRead(NewsHeadline headline)
    {
        if (!headline.IsUnread) return;
        headline.IsUnread = false;
        Context.Host.NewsRead.MarkRead(headline.Id);
        UnreadCount = Headlines.Count(h => h.IsUnread);
    }

    /// <summary>
    /// The feed has nothing to do with commander state, so the tick only kicks off the first load and
    /// then paces the ticker — riding the dashboard's clock rather than starting a timer of its own.
    /// </summary>
    public override void Update(CommanderState s)
    {
        if (!_loadRequested)
        {
            _loadRequested = true;
            LoadAsync().Forget("Galnet: load");
            return;
        }

        if (Headlines.Count < 2) return;   // nothing to rotate through
        if (++_tickCount < TicksPerHeadline) return;

        _tickCount = 0;
        ShowHeadline(_tickerIndex + 1);
    }

    /// <summary>Drop the loaded articles so the next tick refetches — used by "reset to live data".</summary>
    public override void Reset()
    {
        _loadRequested = false;
        _tickCount = 0;
        Headlines.Clear();
        ShowHeadline(0);
        SelectedHeadline = null;
        NewsStatus = "";
        UnreadCount = 0;
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    /// <summary>Clear every unread badge at once, for a commander catching up after time away.</summary>
    [RelayCommand]
    private void MarkAllRead()
    {
        if (Headlines.Count == 0) return;
        Context.Host.NewsRead.MarkAllRead(Headlines.Select(h => h.Id));
        foreach (var headline in Headlines) headline.IsUnread = false;
        UnreadCount = 0;
    }

    /// <summary>Step the ticker by hand, for a commander who wants the previous headline back.</summary>
    [RelayCommand]
    private void PreviousHeadline()
    {
        _tickCount = 0;
        ShowHeadline(_tickerIndex - 1);
    }

    [RelayCommand]
    private void NextHeadline()
    {
        _tickCount = 0;
        ShowHeadline(_tickerIndex + 1);
    }

    /// <summary>
    /// Open the reader on the headline currently showing — the "if interested" half of the ticker.
    /// A second open focuses the window already up rather than stacking another.
    /// </summary>
    [RelayCommand]
    private void OpenReader()
    {
        if (TickerHeadline is { } current)
        {
            SelectedHeadline = current;
            MarkRead(current);   // the change handler only marks read while the window is already up
        }

        if (_reader is not null)
        {
            _reader.Activate();
            return;
        }

        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        var reader = new Views.GalnetWindow { DataContext = this };
        reader.Closed += (_, _) => _reader = null;
        _reader = reader;

        // Owned by the dashboard: the app's shutdown mode closes on the last window, so an unowned
        // reader left open would keep EDNexus running after the main window had gone.
        if (owner is not null) reader.Show(owner);
        else reader.Show();
    }

    /// <summary>Point the ticker at <paramref name="index"/>, wrapping around the feed in both directions.</summary>
    private void ShowHeadline(int index)
    {
        if (Headlines.Count == 0)
        {
            _tickerIndex = 0;
            TickerHeadline = null;
            HasTicker = false;
            TickerPosition = "";
            return;
        }

        _tickerIndex = ((index % Headlines.Count) + Headlines.Count) % Headlines.Count;
        TickerHeadline = Headlines[_tickerIndex];
        HasTicker = true;
        TickerPosition = $"{_tickerIndex + 1} / {Headlines.Count}";
    }

    private async Task LoadAsync()
    {
        if (NewsBusy) return;

        NewsBusy = true;
        NewsStatus = "Fetching Galnet …";
        try
        {
            var feed = Feed;
            var articles = await feed.GetLatestAsync(CancellationToken.None);

            var previous = SelectedHeadline?.Id;
            var onTicker = TickerHeadline?.Id;

            var readTracker = Context.Host.NewsRead;
            Headlines.Clear();
            foreach (var article in articles)
                Headlines.Add(new NewsHeadline(
                    article.Id, article.Title, article.Body, article.Published, readTracker.IsUnread(article.Id)));

            UnreadCount = Headlines.Count(h => h.IsUnread);

            if (Headlines.Count == 0)
            {
                ShowHeadline(0);
                SelectedHeadline = null;
                NewsStatus = $"Nothing from {feed.SourceName} right now.";
                return;
            }

            // Hold the commander's place across a refresh wherever the article is still in the feed.
            SelectedHeadline = Headlines.FirstOrDefault(h => h.Id == previous) ?? Headlines[0];

            var resumeAt = Headlines.ToList().FindIndex(h => h.Id == onTicker);
            _tickCount = 0;
            ShowHeadline(resumeAt < 0 ? 0 : resumeAt);

            NewsStatus = feed.SourceName;
        }
        catch (Exception ex)
        {
            NewsStatus = $"Galnet unavailable: {ex.Message}";
        }
        finally
        {
            NewsBusy = false;
        }
    }
}

/// <summary>
/// One headline, carrying its article so opening the reader needs no second fetch. A class rather than
/// a record because <see cref="IsUnread"/> flips in place once the commander opens it, and the headline
/// list's bindings need to see that change without the item being replaced.
/// </summary>
public sealed partial class NewsHeadline : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public NewsHeadline(string id, string title, string body, DateTimeOffset? published, bool isUnread)
    {
        Id = id;
        Title = title;
        Body = body;
        Published = published;
        _isUnread = isUnread;
    }

    public string Id { get; }
    public string Title { get; }
    public string Body { get; }
    public DateTimeOffset? Published { get; }

    /// <summary>True until the commander opens this headline in the reader (see <see cref="EDNexus.Core.News.NewsReadTracker"/>).</summary>
    [ObservableProperty] private bool _isUnread;

    /// <summary>Short date for the headline row; empty when the feed gave no date.</summary>
    public string Stamp => Published is { } p ? p.ToLocalTime().ToString("d MMM") : "";
}
