using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.Core.Dev;
using EDNexus.Core.Radio;
using EDNexus.Core.State;

namespace EDNexus.App.ViewModels;

/// <summary>
/// Space Radio: the full player for the built-in stations. It shows live/buffering/error status and
/// has play/pause, previous/next, a station picker, and volume with mute. Every control acts on
/// <see cref="RadioPlayerSelector.Active"/>, the same player the title-bar transport drives. That is
/// <see cref="RadioPlayerService"/> in live use, so play/pause from either place keeps the persisted
/// resume-on-launch intent correct.
/// </summary>
/// <remarks>
/// While developer mode is on, the selector hands out a <see cref="SimulatedRadioPlayer"/> instead,
/// fed by <see cref="RadioSampleSource"/> through the real bus via the 🎲. Its states and controls
/// can then be exercised without starting audio, touching the network, or rewriting the saved radio
/// settings.
/// </remarks>
public sealed partial class RadioCardViewModel : CardViewModel
{
    private bool _syncing;

    public RadioCardViewModel(DashboardContext context) : base(context, "radio", "SPACE RADIO", 452) { }

    private IRadioPlayer Player => Context.Radio.Active;

    /// <summary>Every station on offer, in catalog order, for the picker.</summary>
    public IReadOnlyList<RadioStation> Stations => RadioStationCatalog.Stations;

    [ObservableProperty] private RadioStation? _selectedStation;
    [ObservableProperty] private double _volume = 50;
    [ObservableProperty] private bool _isMuted;

    [ObservableProperty] private string _statusLabel = "OFF";
    [ObservableProperty] private string _statusDetail = "Pick a station and press play.";
    [ObservableProperty] private string _playPauseGlyph = RadioDisplay.PlayGlyph;
    [ObservableProperty] private string _playPauseTooltip = "Play the radio";
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private string _muteGlyph = RadioDisplay.LoudGlyph;
    [ObservableProperty] private string _muteTooltip = "Mute";
    [ObservableProperty] private string _volumeText = "50";

    /// <summary>True while the card shows the developer-mode simulation rather than the real player.</summary>
    [ObservableProperty] private bool _isSimulated;

    /// <summary>
    /// The radio has nothing to do with commander state. The tick just re-reads the player snapshot,
    /// which keeps the card in step with the title bar, media keys, and background stream events.
    /// </summary>
    public override void Update(CommanderState state) => Apply(Player.Snapshot);

    private void Apply(RadioPlayerSnapshot s)
    {
        var d = RadioDisplay.From(s);

        _syncing = true;
        try
        {
            IsSimulated = Context.Radio.IsSimulated;
            StatusLabel = d.StatusLabel;
            StatusDetail = d.StatusDetail;
            PlayPauseGlyph = d.PlayPauseGlyph;
            PlayPauseTooltip = d.PlayPauseTooltip;
            IsLive = d.IsLive;
            IsBuffering = d.IsBuffering;
            IsError = d.IsError;
            MuteGlyph = d.MuteGlyph;
            MuteTooltip = d.MuteTooltip;
            IsMuted = s.Muted;
            SelectedStation = s.Station;
            Volume = s.Volume;
            VolumeText = s.Volume.ToString();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Picking a station tunes to it, the same as a radio dial.</summary>
    partial void OnSelectedStationChanged(RadioStation? value)
    {
        if (_syncing || value is null) return;
        Player.PlayAsync(value.Id).Forget("Radio: tune station");
    }

    /// <summary>
    /// Applied to the player on every step so the level changes while dragging. The player updates
    /// its snapshot synchronously, so the next tick doesn't pull the slider back. It also coalesces
    /// the resulting settings writes itself.
    /// </summary>
    partial void OnVolumeChanged(double value)
    {
        if (_syncing) return;

        var level = (int)Math.Round(Math.Clamp(value, 0, 100));
        VolumeText = level.ToString();
        Player.SetVolumeAsync(level).Forget("Radio: set volume");
    }

    /// <summary>Same entry point as the title-bar button and the Play/Pause media key.</summary>
    [RelayCommand]
    private Task PlayPause() => Player.TogglePlayPauseAsync();

    [RelayCommand]
    private Task NextStation() => Player.NextStationAsync();

    [RelayCommand]
    private Task PreviousStation() => Player.PreviousStationAsync();

    [RelayCommand]
    private Task ToggleMute() => Player.SetMuteAsync(!Player.Snapshot.Muted);
}
