namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia;
using Connection;
using Diagnostics;
using Hoppie;
using Network;
using ReactiveUI;
using Simulation;

public class MainViewModel : ReactiveObject, IDisposable
{
    private static readonly HashSet<string> FenixSidestickVars = new(StringComparer.OrdinalIgnoreCase)
    {
        "L:N_FC_SIDESTICK_CAPT_PITCH",
        "L:N_FC_SIDESTICK_CAPT_BANK",
        "L:N_FC_SIDESTICK_FO_PITCH",
        "L:N_FC_SIDESTICK_FO_BANK",
        "L:S_FC_CAPT_INST_DISCONNECT",
        "L:S_FC_FO_INST_DISCONNECT"
    };

    private readonly CompositeDisposable _d = new();
    private readonly CrewSideState _crewSideState;
    private readonly LocalJoystickService _localJoystickService;
    private readonly FenixSidestickProfileState _fenixSidestickProfileState;
    private readonly FenixSidestickSessionState _fenixSidestickSessionState;
    private readonly FenixSidestickCoordinator _fenixSidestickCoordinator;

    private string _aircraft = string.Empty;
    private string _connectionCode = string.Empty;
    private bool _isBusy;
    private bool _connected;
    private bool _showTakeControl;
    private bool _hasControl = true;
    private bool _newProfileAvailable;
    private double _alertVolume = UiSounds.Volume;
    private double _alertRepeatSeconds = UiSounds.ChimeRepeatSeconds;
    private string _gsxStatusText = "GSX sync monitor armed.";
    private string _gsxStatusLevel = "info";
    private string _gsxStatusStamp = DateTime.Now.ToString("HH:mm:ss");
    private string _peerGsxStatusText = "Waiting for peer GSX status.";
    private string _peerGsxStatusLevel = "info";
    private string _peerGsxStatusStamp = "--:--:--";
    private string _movementStatusText = string.Empty;
    private string _movementStatusLevel = "warning";
    private string _movementStatusStamp = "--:--:--";
    private bool _movementStatusVisible;
    private long _movementStatusVersion;
    private string _promptStatusText = string.Empty;
    private string _promptStatusLevel = "warning";
    private string _promptStatusStamp = "--:--:--";
    private bool _promptStatusVisible;
    private readonly GsxSettingsStore _gsxSettings;
    private bool _loadingGsxSettings;
    private bool _gsxSyncEnabled = true;
    private bool _gsxPushbackPositionPauseEnabled = true;
    private bool _gsxPushbackFullDesyncEnabled;
    private bool _showGsxExperimentalNotice;
    private bool _showHoppieHostConfig = true;
    private bool _hoppieEnabled;
    private bool _hoppieTransparentCaptureEnabled;
    private bool _hoppieRecorderModeEnabled;
    private bool _hoppieCaptureInstalled;
    private string _hoppieUpstreamLogon = string.Empty;
    private string _hoppieUpstreamUrl = HoppieSettings.DefaultUpstreamUrl;
    private string _hoppieLocalPort = HoppieSettings.DefaultLocalPort.ToString(CultureInfo.InvariantCulture);
    private string _hoppieStatus = string.Empty;
    private string _hoppieCaptureStatus = string.Empty;
    private string _fenixJoystickStatus = "No joystick selected.";
    private string _fenixJoystickPreview = "Live input preview unavailable.";
    private string _fenixTakeoverButtonStatus = "Takeover button preview unavailable.";
    private string _fenixRuntimeStatus = "Sidestick runtime status unavailable.";
    private double _fenixRollMarkerLeft = 103;
    private double _fenixRollMarkerTop = 103;
    private double _fenixPitchMarkerLeft = 103;
    private double _fenixPitchMarkerTop = 103;
    private ViewErrors _errors = ViewErrors.None;

    private string Aircraft
    {
        set
        {
            _aircraft = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
            this.RaisePropertyChanged(nameof(FenixAircraftLoaded));
            this.RaisePropertyChanged(nameof(FenixLoadedAircraftText));
            this.RaisePropertyChanged(nameof(FenixSidestickActivationText));
            this.RaisePropertyChanged(nameof(FenixOperationalStatusText));
            this.RaisePropertyChanged(nameof(FenixOperationalArmed));
            this.RaisePropertyChanged(nameof(FenixOperationalStandby));
            this.RaisePropertyChanged(nameof(FenixOperationalDisabled));
        }
    }

    private ViewErrors Errors
    {
        get => _errors;
        set
        {
            _errors = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            UpdateHoppieCaptureStatus();
        }
    }

    public bool Connected
    {
        get => _connected;
        set
        {
            this.RaiseAndSetIfChanged(ref _connected, value);
            UpdateHoppieCaptureStatus();
        }
    }

    public bool ShowTakeControl
    {
        get => _showTakeControl;
        set => this.RaiseAndSetIfChanged(ref _showTakeControl, value);
    }

    public bool HasControl
    {
        get => _hasControl;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasControl, value);
            this.RaisePropertyChanged(nameof(LocalGsxSideLabel));
            this.RaisePropertyChanged(nameof(PeerGsxSideLabel));
            this.RaisePropertyChanged(nameof(GsxStatusHeader));
            this.RaisePropertyChanged(nameof(PeerGsxStatusHeader));
        }
    }

    public string ConnectionCode
    {
        get => _connectionCode;
        set => this.RaiseAndSetIfChanged(ref _connectionCode, value);
    }

    public bool NewProfileAvailable
    {
        get => _newProfileAvailable;
        set => this.RaiseAndSetIfChanged(ref _newProfileAvailable, value);
    }

    public bool IsCaptainSide
    {
        get => _crewSideState.SelectedSide == CrewSide.Captain;
        set
        {
            if (!value) return;
            _crewSideState.SelectedSide = CrewSide.Captain;
            RaiseCrewSideChanged();
        }
    }

    public bool IsFirstOfficerSide
    {
        get => _crewSideState.SelectedSide == CrewSide.FirstOfficer;
        set
        {
            if (!value) return;
            _crewSideState.SelectedSide = CrewSide.FirstOfficer;
            RaiseCrewSideChanged();
        }
    }

    public bool FenixAircraftLoaded => FenixAirbusSupport.IsSupportedAircraft(_aircraft);

    public string FenixLoadedAircraftText =>
        string.IsNullOrWhiteSpace(_aircraft)
            ? "Current aircraft: waiting for Microsoft Flight Simulator."
            : $"Current aircraft: {_aircraft}";

    public string FenixSidestickActivationText =>
        !FenixSidestickProfileEnabled
            ? $"A320 sidestick mode is disabled. It only arms when {FenixAirbusSupport.DisplayName} is loaded and both connected clients enable the module."
            : FenixAircraftLoaded
                ? $"A320 sidestick capture is ready locally for {FenixAirbusSupport.DisplayName}. It engages when both connected clients have the module enabled and the bridge ready."
                : $"A320 sidestick mode is enabled, but it stays idle until {FenixAirbusSupport.DisplayName} is loaded.";

    public string FenixOperationalStatusText =>
        !FenixSidestickProfileEnabled
            ? "Feature disabled. FS Copilot will not capture sidestick input."
            : FenixAircraftLoaded
                ? "Feature ready. FS Copilot takes over Airbus sidestick merge logic only after both connected clients are ready."
                : $"Feature standing by. FS Copilot leaves your normal controls alone until {FenixAirbusSupport.DisplayName} is loaded.";

    public bool FenixOperationalArmed => FenixSidestickProfileEnabled && FenixAircraftLoaded;
    public bool FenixOperationalStandby => FenixSidestickProfileEnabled && !FenixAircraftLoaded;
    public bool FenixOperationalDisabled => !FenixSidestickProfileEnabled;
    public bool FenixConsentOverlayVisible => !FenixSidestickProfileEnabled;
    public bool FenixFloatingConsentVisible => false;
    public double FenixSettingsOpacity => FenixConsentOverlayVisible ? 0.32 : 1.0;
    public bool FenixConsentAccepted => _fenixSidestickProfileState.ConsentAccepted;
    public bool FenixSetupChecklistVisible =>
        FenixSidestickProfileEnabled && !_fenixSidestickProfileState.SetupChecklistDismissed;

    public string FenixConsentTitle => _fenixSidestickProfileState.ConsentAccepted
        ? "A320 sidestick mode is currently disabled"
        : "Fenix A320 sidestick mode";

    public string FenixConsentBody => _fenixSidestickProfileState.ConsentAccepted
        ? "Re-enable the Airbus sidestick layer whenever you are back in a Fenix cockpit. Outside the Fenix A319 / A320 / A321 family, FS Copilot leaves your normal controls alone."
        : "This feature is experimental. It replaces the normal single-stream Fenix sidestick path with FS Copilot takeover, merging and priority logic. You can disable it again at any time from this tab.";

    public string FenixConsentButtonText => _fenixSidestickProfileState.ConsentAccepted
        ? "Enable A320 mode again"
        : "I agree, enable A320 mode";

    public string FenixExpectedEfbControlSide =>
        _crewSideState.SelectedSide == CrewSide.Captain ? "Left" : "Right";

    public string FenixSetupSeatText =>
        $"In the Fenix EFB on this client, open Sim Settings > Controls and set the control position to {FenixExpectedEfbControlSide}.";

    public string FenixSetupJoystickText =>
        "On first setup, choose the local joystick below. You can change the device, curve and takeover source at any time later.";

    public string FenixDynamicAuthorityText =>
        FenixDynamicAuthorityEditable
            ? "Optional latency reduction. When a pilot presses the sidestick priority button, FS Copilot can hand control ownership to that side instead of waiting for a full relay loop. The other client will follow this setting automatically."
            : "Controlled by the current FS Copilot control owner. This client mirrors the remote dynamic-authority setting automatically.";

    public string FenixSetupDismissText => "I checked the Fenix EFB side and my joystick selection.";

    public IReadOnlyList<Point> FenixRollCurvePoints => BuildCurvePolylinePoints(
        _fenixSidestickProfileState.RollDeadzone,
        _fenixSidestickProfileState.RollSensitivity,
        _fenixSidestickProfileState.RollExpo,
        _fenixSidestickProfileState.InvertRoll);

    public IReadOnlyList<Point> FenixPitchCurvePoints => BuildCurvePolylinePoints(
        _fenixSidestickProfileState.PitchDeadzone,
        _fenixSidestickProfileState.PitchSensitivity,
        _fenixSidestickProfileState.PitchExpo,
        _fenixSidestickProfileState.InvertPitch);

    public bool FenixSidestickProfileEnabled
    {
        get => _fenixSidestickProfileState.Enabled;
        set => _fenixSidestickProfileState.Enabled = value;
    }

    public double FenixPitchDeadzone
    {
        get => _fenixSidestickProfileState.PitchDeadzone;
        set => _fenixSidestickProfileState.PitchDeadzone = value;
    }

    public double FenixRollDeadzone
    {
        get => _fenixSidestickProfileState.RollDeadzone;
        set => _fenixSidestickProfileState.RollDeadzone = value;
    }

    public double FenixPitchSensitivity
    {
        get => _fenixSidestickProfileState.PitchSensitivity;
        set => _fenixSidestickProfileState.PitchSensitivity = value;
    }

    public double FenixRollSensitivity
    {
        get => _fenixSidestickProfileState.RollSensitivity;
        set => _fenixSidestickProfileState.RollSensitivity = value;
    }

    public double FenixPitchExpo
    {
        get => _fenixSidestickProfileState.PitchExpo;
        set => _fenixSidestickProfileState.PitchExpo = value;
    }

    public double FenixRollExpo
    {
        get => _fenixSidestickProfileState.RollExpo;
        set => _fenixSidestickProfileState.RollExpo = value;
    }

    public bool FenixInvertPitch
    {
        get => _fenixSidestickProfileState.InvertPitch;
        set => _fenixSidestickProfileState.InvertPitch = value;
    }

    public bool FenixInvertRoll
    {
        get => _fenixSidestickProfileState.InvertRoll;
        set => _fenixSidestickProfileState.InvertRoll = value;
    }

    public int FenixControllerIndex
    {
        get => _fenixSidestickProfileState.ControllerIndex;
        set
        {
            _fenixSidestickProfileState.ControllerIndex = value;
            UpdateFenixJoystickStatus();
        }
    }

    public int FenixTakeoverButtonIndex
    {
        get => _fenixSidestickProfileState.TakeoverButtonIndex;
        set
        {
            _fenixSidestickProfileState.TakeoverButtonIndex = value;
            this.RaisePropertyChanged(nameof(FenixTakeoverButtonSelectionIndex));
            UpdateFenixJoystickStatus();
        }
    }

    public int FenixTakeoverButtonSelectionIndex
    {
        get => _fenixSidestickProfileState.TakeoverButtonIndex + 1;
        set
        {
            _fenixSidestickProfileState.TakeoverButtonIndex = Math.Clamp(value - 1, -1, 31);
            this.RaisePropertyChanged(nameof(FenixTakeoverButtonIndex));
            UpdateFenixJoystickStatus();
        }
    }

    public bool FenixDynamicAuthorityEnabled
    {
        get => _fenixSidestickCoordinator.GetEffectiveDynamicAuthorityEnabled();
        set
        {
            if (!FenixDynamicAuthorityEditable)
                return;

            _fenixSidestickProfileState.DynamicAuthorityEnabled = value;
        }
    }

    public bool FenixDynamicAuthorityEditable =>
        _fenixSidestickCoordinator.IsDynamicAuthorityEditable();

    public string FenixJoystickStatus
    {
        get => _fenixJoystickStatus;
        private set => this.RaiseAndSetIfChanged(ref _fenixJoystickStatus, value);
    }

    public string FenixJoystickPreview
    {
        get => _fenixJoystickPreview;
        private set => this.RaiseAndSetIfChanged(ref _fenixJoystickPreview, value);
    }

    public string FenixTakeoverButtonStatus
    {
        get => _fenixTakeoverButtonStatus;
        private set => this.RaiseAndSetIfChanged(ref _fenixTakeoverButtonStatus, value);
    }

    public string FenixRuntimeStatus
    {
        get => _fenixRuntimeStatus;
        private set => this.RaiseAndSetIfChanged(ref _fenixRuntimeStatus, value);
    }

    public double FenixRollMarkerLeft
    {
        get => _fenixRollMarkerLeft;
        private set => this.RaiseAndSetIfChanged(ref _fenixRollMarkerLeft, value);
    }

    public double FenixRollMarkerTop
    {
        get => _fenixRollMarkerTop;
        private set => this.RaiseAndSetIfChanged(ref _fenixRollMarkerTop, value);
    }

    public double FenixPitchMarkerLeft
    {
        get => _fenixPitchMarkerLeft;
        private set => this.RaiseAndSetIfChanged(ref _fenixPitchMarkerLeft, value);
    }

    public double FenixPitchMarkerTop
    {
        get => _fenixPitchMarkerTop;
        private set => this.RaiseAndSetIfChanged(ref _fenixPitchMarkerTop, value);
    }

    public ObservableCollection<JoystickDeviceOption> FenixJoystickDevices { get; } = [];
    public ObservableCollection<ButtonOption> FenixTakeoverButtons { get; } = [];

    public double AlertVolume
    {
        get => _alertVolume;
        set
        {
            this.RaiseAndSetIfChanged(ref _alertVolume, value);
            UiSounds.Volume = value;
            this.RaisePropertyChanged(nameof(AlertVolumePercent));
        }
    }

    public int AlertVolumePercent => (int)Math.Round(AlertVolume * 100);

    public double AlertRepeatSeconds
    {
        get => _alertRepeatSeconds;
        set
        {
            var rounded = Math.Clamp(Math.Round(value), 1.0, 30.0);
            this.RaiseAndSetIfChanged(ref _alertRepeatSeconds, rounded);
            UiSounds.ChimeRepeatSeconds = rounded;
            this.RaisePropertyChanged(nameof(AlertRepeatSecondsDisplay));
        }
    }

    public string AlertRepeatSecondsDisplay => $"Repeat chime every {AlertRepeatSeconds:0}s";

    public bool GsxSyncEnabled
    {
        get => _gsxSyncEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _gsxSyncEnabled, value);
            this.RaisePropertyChanged(nameof(GsxSyncSummary));
            SaveGsxSettings();
        }
    }

    public bool GsxPushbackPositionPauseEnabled
    {
        get => _gsxPushbackPositionPauseEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _gsxPushbackPositionPauseEnabled, value);
            this.RaisePropertyChanged(nameof(GsxPushbackPositionPauseSummary));
            SaveGsxSettings();
        }
    }

    public bool GsxPushbackFullDesyncEnabled
    {
        get => _gsxPushbackFullDesyncEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _gsxPushbackFullDesyncEnabled, value);
            this.RaisePropertyChanged(nameof(GsxPushbackFullDesyncSummary));
            SaveGsxSettings();
        }
    }

    public bool ShowGsxExperimentalNotice
    {
        get => _showGsxExperimentalNotice;
        private set => this.RaiseAndSetIfChanged(ref _showGsxExperimentalNotice, value);
    }

    public string GsxSyncSummary => GsxSyncEnabled
        ? "GSX sync is enabled. FS Copilot will mirror supported GSX menus, services, parking, and cues."
        : "GSX sync is disabled. FS Copilot will not automate peer GSX menus or service states.";

    public string GsxPushbackPositionPauseSummary => GsxPushbackPositionPauseEnabled
        ? "Experimental: position packets pause during GSX pushback/towing so each sim can let its own tug drive."
        : "Position packets stay live during GSX pushback/towing. Useful for troubleshooting tug desync.";

    public string GsxPushbackFullDesyncSummary => GsxPushbackFullDesyncEnabled
        ? "Experimental: pushback/towing requests are not sent to the peer. Position sync also stays live."
        : "Pushback/towing requests are synchronized normally.";

    public bool ShowHoppieHostConfig
    {
        get => _showHoppieHostConfig;
        set => this.RaiseAndSetIfChanged(ref _showHoppieHostConfig, value);
    }

    public bool HoppieEnabled
    {
        get => _hoppieEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _hoppieEnabled, value);
            this.RaisePropertyChanged(nameof(HoppieToggleButtonText));
            this.RaisePropertyChanged(nameof(HoppieModeSummary));
        }
    }

    public bool HoppieTransparentCaptureEnabled
    {
        get => _hoppieTransparentCaptureEnabled;
        set => this.RaiseAndSetIfChanged(ref _hoppieTransparentCaptureEnabled, value);
    }

    public bool HoppieRecorderModeEnabled
    {
        get => _hoppieRecorderModeEnabled;
        set => this.RaiseAndSetIfChanged(ref _hoppieRecorderModeEnabled, value);
    }

    public string HoppieUpstreamLogon
    {
        get => _hoppieUpstreamLogon;
        set => this.RaiseAndSetIfChanged(ref _hoppieUpstreamLogon, value);
    }

    public string HoppieUpstreamUrl
    {
        get => _hoppieUpstreamUrl;
        set => this.RaiseAndSetIfChanged(ref _hoppieUpstreamUrl, value);
    }

    public string HoppieLocalPort
    {
        get => _hoppieLocalPort;
        set
        {
            this.RaiseAndSetIfChanged(ref _hoppieLocalPort, value);
            this.RaisePropertyChanged(nameof(HoppieLocalUrl));
        }
    }

    public string HoppieLocalUrl =>
        $"http://127.0.0.1:{NormalizeLocalPort(HoppieLocalPort).ToString(CultureInfo.InvariantCulture)}/acars/system/connect.html";

    public string HoppieToggleButtonText => HoppieEnabled ? "Turn Off Hoppie" : "Turn On Hoppie";

    public string HoppieModeSummary => HoppieEnabled
        ? "Hoppie sync is enabled. FS Copilot can manage the local capture redirect while connected."
        : "Hoppie sync is off. FS Copilot will not request admin rights for Hoppie capture.";

    public string HoppieStatus
    {
        get => _hoppieStatus;
        set => this.RaiseAndSetIfChanged(ref _hoppieStatus, value);
    }

    public string HoppieCaptureStatus
    {
        get => _hoppieCaptureStatus;
        set => this.RaiseAndSetIfChanged(ref _hoppieCaptureStatus, value);
    }

    public string GsxStatusText
    {
        get => _gsxStatusText;
        private set => this.RaiseAndSetIfChanged(ref _gsxStatusText, value);
    }

    public string GsxStatusStamp
    {
        get => _gsxStatusStamp;
        private set => this.RaiseAndSetIfChanged(ref _gsxStatusStamp, value);
    }

    public string GsxStatusLevel
    {
        get => _gsxStatusLevel;
        private set
        {
            this.RaiseAndSetIfChanged(ref _gsxStatusLevel, value);
            this.RaisePropertyChanged(nameof(GsxStatusHeader));
            this.RaisePropertyChanged(nameof(GsxStatusBackground));
            this.RaisePropertyChanged(nameof(GsxStatusBadgeBackground));
            this.RaisePropertyChanged(nameof(GsxStatusAccent));
        }
    }

    public string PeerGsxStatusText
    {
        get => _peerGsxStatusText;
        private set => this.RaiseAndSetIfChanged(ref _peerGsxStatusText, value);
    }

    public string PeerGsxStatusStamp
    {
        get => _peerGsxStatusStamp;
        private set => this.RaiseAndSetIfChanged(ref _peerGsxStatusStamp, value);
    }

    public string PeerGsxStatusLevel
    {
        get => _peerGsxStatusLevel;
        private set
        {
            this.RaiseAndSetIfChanged(ref _peerGsxStatusLevel, value);
            this.RaisePropertyChanged(nameof(PeerGsxStatusHeader));
            this.RaisePropertyChanged(nameof(PeerGsxStatusBackground));
            this.RaisePropertyChanged(nameof(PeerGsxStatusAccent));
        }
    }

    public string MovementStatusText
    {
        get => _movementStatusText;
        private set => this.RaiseAndSetIfChanged(ref _movementStatusText, value);
    }

    public string MovementStatusStamp
    {
        get => _movementStatusStamp;
        private set => this.RaiseAndSetIfChanged(ref _movementStatusStamp, value);
    }

    public string MovementStatusLevel
    {
        get => _movementStatusLevel;
        private set
        {
            this.RaiseAndSetIfChanged(ref _movementStatusLevel, value);
            this.RaisePropertyChanged(nameof(MovementStatusBadgeBackground));
        }
    }

    public bool MovementStatusVisible
    {
        get => _movementStatusVisible;
        private set => this.RaiseAndSetIfChanged(ref _movementStatusVisible, value);
    }

    public string PromptStatusText
    {
        get => _promptStatusText;
        private set => this.RaiseAndSetIfChanged(ref _promptStatusText, value);
    }

    public string PromptStatusStamp
    {
        get => _promptStatusStamp;
        private set => this.RaiseAndSetIfChanged(ref _promptStatusStamp, value);
    }

    public string PromptStatusLevel
    {
        get => _promptStatusLevel;
        private set
        {
            this.RaiseAndSetIfChanged(ref _promptStatusLevel, value);
            this.RaisePropertyChanged(nameof(PromptStatusHeader));
            this.RaisePropertyChanged(nameof(PromptStatusBackground));
            this.RaisePropertyChanged(nameof(PromptStatusBadgeBackground));
            this.RaisePropertyChanged(nameof(PromptStatusAccent));
        }
    }

    public bool PromptStatusVisible
    {
        get => _promptStatusVisible;
        private set => this.RaiseAndSetIfChanged(ref _promptStatusVisible, value);
    }

    public string LocalGsxSideLabel => HasControl ? "THIS PC - MASTER / controls" : "THIS PC - SLAVE / monitor";
    public string PeerGsxSideLabel => HasControl ? "PEER - SLAVE / monitor" : "PEER - MASTER / controls";

    public string GsxStatusHeader => $"{LocalGsxSideLabel} - {ToGsxStatusHeader(GsxStatusLevel, "Local GSX")}";
    public string PeerGsxStatusHeader => $"{PeerGsxSideLabel} - {ToGsxStatusHeader(PeerGsxStatusLevel, "Peer GSX")}";
    public string PromptStatusHeader => ToGsxStatusHeader(PromptStatusLevel, "GSX Cue");

    public string GsxStatusBackground => ToGsxStatusBackground(GsxStatusLevel);
    public string PeerGsxStatusBackground => ToGsxStatusBackground(PeerGsxStatusLevel);
    public string PromptStatusBackground => ToGsxStatusBackground(PromptStatusLevel);
    public string GsxStatusBadgeBackground => ToGsxStatusBadgeBackground(GsxStatusLevel);
    public string MovementStatusBadgeBackground => ToGsxMovementBadgeBackground(MovementStatusLevel);
    public string PromptStatusBadgeBackground => ToGsxStatusBadgeBackground(PromptStatusLevel);

    public string GsxStatusAccent => ToGsxStatusAccent(GsxStatusLevel);
    public string PeerGsxStatusAccent => ToGsxStatusAccent(PeerGsxStatusLevel);
    public string PromptStatusAccent => ToGsxStatusAccent(PromptStatusLevel);

    private static string ToGsxStatusHeader(string level, string fallback) => level switch
    {
        "error" => "GSX Blocked",
        "warning" => "GSX Warning",
        "success" => "GSX Synced",
        "action" => "GSX Action",
        _ => fallback
    };

    private static string ToGsxStatusBackground(string level) => level switch
    {
        "error" => "#66501b1b",
        "warning" => "#664f2d00",
        "success" => "#66335c35",
        "action" => "#66345375",
        _ => "#662f4858"
    };

    private static string ToGsxStatusBadgeBackground(string level) => level switch
    {
        "error" => "#f05b5b",
        "warning" => "#e0a33a",
        "success" => "#8fc58f",
        "action" => "#8ec6ff",
        _ => "#8fc58f"
    };

    private static string ToGsxMovementBadgeBackground(string level) => level switch
    {
        "warning" => "#d8b168",
        "success" => "#8fc58f",
        "action" => "#8ec6ff",
        _ => "#8fc58f"
    };

    private static string ToGsxStatusAccent(string level) => level switch
    {
        "error" => "#ff4d4d",
        "warning" => "#ffb020",
        "success" => "#70d66b",
        "action" => "#69b7ff",
        _ => "#8ec6ff"
    };

    public string PeerId { get; init; }
    public string ClientName { get; init; }

    public string Version => App.Version;
    public string VersionDisplay => App.VersionDisplay;
    public string BuildLabel => App.BuildLabel;
    public string WindowTitle => App.WindowTitle;
    public string NetworkMode { get; }
    public string ErrorMessage =>
        _errors.HasFlag(ViewErrors.Failed) ? "Failed to connect." :
        _errors.HasFlag(ViewErrors.NotRunning) ? "Microsoft Flight Simulator is not running!" :
        _errors.HasFlag(ViewErrors.NotLoadedBridge) ? "Bridge package is not loaded. Check your Community folder." :
        _errors.HasFlag(ViewErrors.BridgeMismatch) ? "Bridge version mismatch. Update Community package." :
        _errors.HasFlag(ViewErrors.NotSupported) ? $"No profile available for the {_aircraft}." :
        _errors.HasFlag(ViewErrors.Rejected) ? "Both sides must use the same FS Copilot version." :
        _errors.HasFlag(ViewErrors.Conflict) ? "Conflict detected with YourControls package." :
        string.Empty;

    public ObservableCollection<Connection> Connections { get; set; } = [];
    public ReactiveCommand<Unit, Unit> JoinCommand { get; }
    public ReactiveCommand<Unit, Unit> LeaveCommand { get; }
    public ReactiveCommand<Unit, Unit> TakeControlCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> SilenceGsxCueCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissGsxExperimentalNoticeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleHoppieCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveHoppieCommand => ToggleHoppieCommand;
    public ReactiveCommand<Unit, Unit> EnableFenixSidestickProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> DisableFenixSidestickProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissFenixSetupChecklistCommand { get; }
    public ReactiveCommand<string, Unit> ResetFenixAxisSettingCommand { get; }
    public event Action<HoppieRestartPrompt>? HoppieRestartPromptRequested;

    public MainViewModel(string peerId,
        string name,
        INetwork net,
        SimClient sim,
        MasterSwitch masterSwitch,
        SessionLogSnapshotService sessionLogSnapshot,
        Coordinator coordinator,
        GsxSettingsStore gsxSettings,
        Updater updater,
        string networkMode,
        SessionRole sessionRole,
        HoppieSettingsStore hoppieSettings,
        HoppieCaptureAutomation hoppieCaptureAutomation,
        HoppieProxyService hoppieProxy,
        HoppieTransparentCaptureManager hoppieCapture,
        CrewSideState crewSideState,
        LocalJoystickService localJoystickService,
        FenixSidestickProfileState fenixSidestickProfileState,
        FenixSidestickCoordinator fenixSidestickCoordinator,
        FenixSidestickSessionState fenixSidestickSessionState)
    {
        _crewSideState = crewSideState;
        _localJoystickService = localJoystickService;
        _fenixSidestickProfileState = fenixSidestickProfileState;
        _fenixSidestickCoordinator = fenixSidestickCoordinator;
        _fenixSidestickSessionState = fenixSidestickSessionState;
        ClientName = name;
        PeerId = peerId;
        NetworkMode = networkMode;
        _gsxSettings = gsxSettings;
        SilenceGsxCueCommand = ReactiveCommand.Create(UiSounds.SilenceRepeatingAlerts);
        DismissGsxExperimentalNoticeCommand = ReactiveCommand.Create(DismissGsxExperimentalNotice);

        FenixTakeoverButtons.Add(new ButtonOption(-1, "Automatic from local Fenix red button"));
        for (var index = 0; index < 32; index++)
            FenixTakeoverButtons.Add(new ButtonOption(index, $"Button {index + 1}"));

        ApplyGsxSettings(gsxSettings.Current);

        var localHoppieSettings = hoppieSettings.Current;
        HoppieEnabled = localHoppieSettings.Enabled;
        HoppieTransparentCaptureEnabled = localHoppieSettings.TransparentCaptureEnabled;
        HoppieRecorderModeEnabled = localHoppieSettings.RecorderModeEnabled;
        HoppieUpstreamLogon = localHoppieSettings.UpstreamLogon;
        HoppieUpstreamUrl = localHoppieSettings.UpstreamUrl;
        HoppieLocalPort = localHoppieSettings.LocalPort.ToString(CultureInfo.InvariantCulture);
        HoppieStatus = hoppieProxy.CurrentStatus.Message;
        _hoppieCaptureInstalled = hoppieCapture.Current.HostsOverrideInstalled;
        ShowHoppieHostConfig = !sessionRole.IsJoiner;
        UpdateHoppieCaptureStatus();

        gsxSettings.Changes
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ApplyGsxSettings)
            .DisposeWith(_d);

        GsxStatusCenter.Events
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                var level = string.IsNullOrWhiteSpace(status.Level) ? "info" : status.Level.Trim().ToLowerInvariant();
                if (string.Equals(status.Source, "movement", StringComparison.OrdinalIgnoreCase))
                {
                    if (level == "clear" || string.IsNullOrWhiteSpace(status.Message))
                    {
                        ++_movementStatusVersion;
                        MovementStatusVisible = false;
                        return;
                    }

                    var version = ++_movementStatusVersion;
                    MovementStatusText = status.Message;
                    MovementStatusLevel = level;
                    MovementStatusStamp = status.Timestamp.LocalDateTime.ToString("HH:mm:ss");
                    MovementStatusVisible = true;

                    if (level == "success")
                    {
                        Observable.Timer(TimeSpan.FromSeconds(8))
                            .ObserveOn(RxApp.MainThreadScheduler)
                            .Subscribe(_ =>
                            {
                                if (_movementStatusVersion == version)
                                    MovementStatusVisible = false;
                            })
                            .DisposeWith(_d);
                    }

                    return;
                }

                if (string.Equals(status.Source, "prompt", StringComparison.OrdinalIgnoreCase))
                {
                    if (level == "clear" || string.IsNullOrWhiteSpace(status.Message))
                    {
                        PromptStatusVisible = false;
                        return;
                    }

                    PromptStatusText = status.Message;
                    PromptStatusLevel = level;
                    PromptStatusStamp = status.Timestamp.LocalDateTime.ToString("HH:mm:ss");
                    PromptStatusVisible = true;
                    return;
                }

                if (string.Equals(status.Source, "peer", StringComparison.OrdinalIgnoreCase))
                {
                    PeerGsxStatusText = status.Message;
                    PeerGsxStatusLevel = level;
                    PeerGsxStatusStamp = status.Timestamp.LocalDateTime.ToString("HH:mm:ss");
                    return;
                }

                GsxStatusText = status.Message;
                GsxStatusLevel = level;
                GsxStatusStamp = status.Timestamp.LocalDateTime.ToString("HH:mm:ss");
            })
            .DisposeWith(_d);

        sim.Aircraft
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(aircraft => Aircraft = aircraft)
            .DisposeWith(_d);

        crewSideState.WhenAnyValue(state => state.SelectedSide)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RaiseCrewSideChanged())
            .DisposeWith(_d);

        fenixSidestickProfileState.Changed
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RaiseFenixSidestickProfileChanged())
            .DisposeWith(_d);

        _fenixSidestickSessionState.Changed
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RaiseFenixDynamicAuthorityStateChanged())
            .DisposeWith(_d);

        localJoystickService.Devices
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(UpdateFenixJoystickDevices)
            .DisposeWith(_d);

        Observable.Interval(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateFenixJoystickPreview())
            .DisposeWith(_d);

       var definitions = new BehaviorSubject<Definitions?>(null);
       sim.Aircraft
            .Select(FenixAirbusSupport.GetDefinitionProfileName)
            .Select(Definitions.Load)
            .Select(FilterDefinitions)
            .Do(defs => Log.Information("[Definitions] Loaded profile {Name} with {Count} links", defs.Name, defs.Count))
            .Subscribe(defs => definitions.OnNext(defs))
            .DisposeWith(_d);

        definitions.Subscribe(defs => Errors = defs is { Count: 0 }
                ? _errors | ViewErrors.NotSupported
                : _errors & ~ViewErrors.NotSupported).DisposeWith(_d);

        definitions
            .Where(defs => defs != null)
            .Subscribe(defs => coordinator.Load(defs!))
            .DisposeWith(_d);

        definitions
            .Where(defs => defs != null)
            .Do(defs => Console.WriteLine(defs?.UpdatedAt))
            .SelectMany(defs => Observable.FromAsync(ct => updater.Check(defs!.Name, ct))
                .Select(updatedAt => updatedAt != null && updatedAt > defs!.UpdatedAt))
            .Subscribe(updateAvailable => NewProfileAvailable =  updateAvailable).DisposeWith(_d);

        sim.Connected
            .Sample(TimeSpan.FromMilliseconds(250))
            .Select(connected => !connected)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(notRunning => Errors = notRunning
                ? _errors | ViewErrors.NotRunning
                : _errors & ~ViewErrors.NotRunning)
            .DisposeWith(_d);

        sim.WasmReady
            .Sample(TimeSpan.FromMilliseconds(250))
            .Select(connected => !connected)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(notLoaded => Errors = notLoaded
                ? _errors | ViewErrors.NotLoadedBridge
                : _errors & ~ViewErrors.NotLoadedBridge)
            .DisposeWith(_d);

        sim.WasmVersionMismatch
            .Sample(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(notLoaded => Errors = notLoaded
                ? _errors | ViewErrors.BridgeMismatch
                : _errors & ~ViewErrors.BridgeMismatch)
            .DisposeWith(_d);

        sim.Conflict
            .Sample(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(conflict => Errors = conflict
                ? _errors | ViewErrors.Conflict
                : _errors & ~ViewErrors.Conflict)
            .DisposeWith(_d);

        net.Peers
            .Sample(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(peers =>
            {
                var i = 0;
                Connections.Clear();
                foreach (var peer in peers) Connections.Add(new(
                    PeerId: peer.PeerId,
                    Name: string.IsNullOrWhiteSpace(peer.Name) ? "Unknown" : peer.Name,
                    Ping: peer.Ping,
                    IsDirect: peer.Transport == Peer.TransportKind.Direct,
                    HasSeparatorAfter: i++ < peers.Count - 1
                ));
                Connected = Connections.Any();
                RaiseFenixDynamicAuthorityStateChanged();
            })
            .DisposeWith(_d);

        net.Peers
            .Select(p => p.Count)
            .DistinctUntilChanged()
            .Scan(
                seed: (Prev: 0, Curr: 0),
                accumulator: (state, curr) => (Prev: state.Curr, Curr: curr))
            .Subscribe(x =>
            {
                if (x.Curr > x.Prev) UiSounds.Play("connect");
                else if (x.Curr < x.Prev) UiSounds.Play("disconnect");
            })
            .DisposeWith(_d);

        masterSwitch.Master
            .Sample(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isMaster =>
            {
                HasControl = isMaster;
                ShowTakeControl = !isMaster;
                RaiseFenixDynamicAuthorityStateChanged();
            })
            .DisposeWith(_d);

        hoppieProxy.StatusChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                HoppieStatus = status.Message;
                this.RaisePropertyChanged(nameof(HoppieLocalUrl));
            })
            .DisposeWith(_d);

        hoppieCapture.Changes
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                _hoppieCaptureInstalled = status.HostsOverrideInstalled;
                UpdateHoppieCaptureStatus();
            })
            .DisposeWith(_d);

        sessionRole.Changes
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isJoiner =>
            {
                ShowHoppieHostConfig = !isJoiner;
                UpdateHoppieCaptureStatus();
            })
            .DisposeWith(_d);

        EnableFenixSidestickProfileCommand = ReactiveCommand.Create(() =>
        {
            var firstConsent = !_fenixSidestickProfileState.ConsentAccepted;
            _fenixSidestickProfileState.ConsentAccepted = true;
            if (firstConsent)
                _fenixSidestickProfileState.SetupChecklistDismissed = false;
            _fenixSidestickProfileState.Enabled = true;
        });

        DisableFenixSidestickProfileCommand = ReactiveCommand.Create(() =>
        {
            _fenixSidestickProfileState.Enabled = false;
        });

        DismissFenixSetupChecklistCommand = ReactiveCommand.Create(() =>
        {
            _fenixSidestickProfileState.SetupChecklistDismissed = true;
        });

        ResetFenixAxisSettingCommand = ReactiveCommand.Create<string>(ResetFenixAxisSetting);

        JoinCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (IsBusy) return;
            if (ConnectionCode.Length != 8) return;

            Errors &= ~ViewErrors.Failed;
            IsBusy = true;
            var preparedJoin = false;
            var result = ConnectionResult.Failed;
            try
            {
                await hoppieCaptureAutomation.PrepareForJoinAsync();
                preparedJoin = true;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

                sessionRole.MarkJoiner();
                masterSwitch.Join();
                result = await net.Connect(ConnectionCode, cts.Token);
            }
            catch
            {
                if (preparedJoin)
                    await hoppieCaptureAutomation.CancelPreparedJoinAsync();

                sessionRole.Reset();
                throw;
            }
            finally
            {
                IsBusy = false;
            }

            if (result == ConnectionResult.Failed)
            {
                if (preparedJoin)
                    await hoppieCaptureAutomation.CancelPreparedJoinAsync();

                sessionRole.Reset();
                Errors |= ViewErrors.Failed;
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Errors &= ~ViewErrors.Failed);
            }
            else if (result == ConnectionResult.Rejected)
            {
                if (preparedJoin)
                    await hoppieCaptureAutomation.CancelPreparedJoinAsync();

                sessionRole.Reset();
                Errors |= ViewErrors.Rejected;
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Errors &= ~ViewErrors.Rejected);
            }
        });

        LeaveCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (IsBusy) return;

            IsBusy = true;
            try
            {
                await sessionLogSnapshot.CaptureAndExchangeAsync();
            }
            finally
            {
                net.Disconnect();
                sessionRole.Reset();
                masterSwitch.TakeControl();
                IsBusy = false;
            }
        });

        TakeControlCommand = ReactiveCommand.Create(masterSwitch.TakeControl);

        DownloadProfileCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            if (definitions.Value == null) return;
            var cfg = await updater.Download(definitions.Value.Name, ct);
            if (cfg == null) return;
            var defs = Definitions.Save(definitions.Value.Name, cfg);
            definitions.OnNext(defs);
        });

        ToggleHoppieCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var normalizedPort = NormalizeLocalPort(HoppieLocalPort);
            HoppieLocalPort = normalizedPort.ToString(CultureInfo.InvariantCulture);
            var shouldEnable = !HoppieEnabled;

            hoppieSettings.Save(new HoppieSettings
            {
                Enabled = shouldEnable,
                TransparentCaptureEnabled = shouldEnable,
                RecorderModeEnabled = false,
                AdminCaptureOptInVersion = 1,
                UpstreamLogon = HoppieUpstreamLogon,
                UpstreamUrl = HoppieUpstreamUrl,
                LocalPort = normalizedPort
            });

            HoppieEnabled = shouldEnable;
            HoppieTransparentCaptureEnabled = shouldEnable;
            HoppieRecorderModeEnabled = false;
            if (!shouldEnable)
            {
                await hoppieCaptureAutomation.CancelPreparedJoinAsync().ConfigureAwait(true);
            }
            else if (HoppieTransparentCaptureManager.IsAdministrator && Connected)
            {
                await hoppieCaptureAutomation.PrepareForJoinAsync().ConfigureAwait(true);
            }

            UpdateHoppieCaptureStatus();
            HoppieRestartPromptRequested?.Invoke(HoppieRestartPrompt.ForState(shouldEnable));
        });
    }

    public void Dispose() => _d.Dispose();

    private void RaiseCrewSideChanged()
    {
        this.RaisePropertyChanged(nameof(IsCaptainSide));
        this.RaisePropertyChanged(nameof(IsFirstOfficerSide));
        this.RaisePropertyChanged(nameof(FenixExpectedEfbControlSide));
        this.RaisePropertyChanged(nameof(FenixSetupSeatText));
    }

    private void RaiseFenixSidestickProfileChanged()
    {
        this.RaisePropertyChanged(nameof(FenixSidestickProfileEnabled));
        this.RaisePropertyChanged(nameof(FenixConsentOverlayVisible));
        this.RaisePropertyChanged(nameof(FenixFloatingConsentVisible));
        this.RaisePropertyChanged(nameof(FenixSettingsOpacity));
        this.RaisePropertyChanged(nameof(FenixConsentAccepted));
        this.RaisePropertyChanged(nameof(FenixSetupChecklistVisible));
        this.RaisePropertyChanged(nameof(FenixConsentTitle));
        this.RaisePropertyChanged(nameof(FenixConsentBody));
        this.RaisePropertyChanged(nameof(FenixConsentButtonText));
        this.RaisePropertyChanged(nameof(FenixSidestickActivationText));
        this.RaisePropertyChanged(nameof(FenixOperationalStatusText));
        this.RaisePropertyChanged(nameof(FenixOperationalArmed));
        this.RaisePropertyChanged(nameof(FenixOperationalStandby));
        this.RaisePropertyChanged(nameof(FenixOperationalDisabled));
        this.RaisePropertyChanged(nameof(FenixRollCurvePoints));
        this.RaisePropertyChanged(nameof(FenixPitchCurvePoints));
        this.RaisePropertyChanged(nameof(FenixPitchDeadzone));
        this.RaisePropertyChanged(nameof(FenixRollDeadzone));
        this.RaisePropertyChanged(nameof(FenixPitchSensitivity));
        this.RaisePropertyChanged(nameof(FenixRollSensitivity));
        this.RaisePropertyChanged(nameof(FenixPitchExpo));
        this.RaisePropertyChanged(nameof(FenixRollExpo));
        this.RaisePropertyChanged(nameof(FenixInvertPitch));
        this.RaisePropertyChanged(nameof(FenixInvertRoll));
        this.RaisePropertyChanged(nameof(FenixControllerIndex));
        this.RaisePropertyChanged(nameof(FenixTakeoverButtonIndex));
        this.RaisePropertyChanged(nameof(FenixTakeoverButtonSelectionIndex));
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityEnabled));
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityEditable));
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityText));
        UpdateFenixJoystickStatus();
        UpdateFenixJoystickPreview();
    }

    private void RaiseFenixDynamicAuthorityStateChanged()
    {
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityEnabled));
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityEditable));
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityText));
    }

    private void UpdateFenixJoystickDevices(IReadOnlyList<LocalJoystickService.JoystickDeviceInfo> devices)
    {
        var byIndex = devices.ToDictionary(device => device.Index, device => device.Name);
        FenixJoystickDevices.Clear();

        for (var index = 0; index <= 7; index++)
        {
            var displayName = byIndex.TryGetValue(index, out var name)
                ? name
                : $"Joystick {index}: not detected";
            FenixJoystickDevices.Add(new JoystickDeviceOption(index, displayName));
        }

        UpdateFenixJoystickStatus();
    }

    private void UpdateFenixJoystickStatus()
    {
        var selected = FenixJoystickDevices.FirstOrDefault(device => device.Index == _fenixSidestickProfileState.ControllerIndex);
        FenixJoystickStatus = selected == null
            ? $"Selected joystick {FenixControllerIndex}."
            : $"Selected device: {selected.DisplayName}";
        FenixTakeoverButtonStatus = _fenixSidestickProfileState.TakeoverButtonIndex < 0
            ? "Takeover source: automatic from the local Fenix red button. FS Copilot reads the real hardware press, suppresses the continued native Fenix action, and keeps takeover logic local to this client."
            : $"Takeover source: local Button {_fenixSidestickProfileState.TakeoverButtonIndex + 1}.";
    }

    private void UpdateFenixJoystickPreview()
    {
        if (_localJoystickService.TryReadSample(_fenixSidestickProfileState.ControllerIndex, out var sample))
        {
            var pressedButtons = DescribePressedButtons(sample.Buttons);
            var takeoverPressed = IsConfiguredButtonPressed(sample.Buttons, _fenixSidestickProfileState.TakeoverButtonIndex);
            UpdateCurveMarkers(sample);
            FenixJoystickPreview =
                $"Live input preview: pitch {-sample.YAxis:0.00}, roll {sample.XAxis:0.00}, buttons {pressedButtons}.";
            FenixTakeoverButtonStatus = _fenixSidestickProfileState.TakeoverButtonIndex < 0
                ? "Takeover source: automatic from the local Fenix red button. FS Copilot reads the real hardware press, suppresses the continued native Fenix action, and keeps takeover logic local to this client."
                : $"Takeover source local Button {_fenixSidestickProfileState.TakeoverButtonIndex + 1}: {(takeoverPressed ? "pressed" : "released")}.";
        }
        else
        {
            ResetCurveMarkers();
            FenixJoystickPreview = $"No live input detected for joystick {_fenixSidestickProfileState.ControllerIndex}.";
            FenixTakeoverButtonStatus = _fenixSidestickProfileState.TakeoverButtonIndex < 0
                ? "Takeover source: automatic from the local Fenix red button. FS Copilot reads the real hardware press, suppresses the continued native Fenix action, and keeps takeover logic local to this client."
                : $"Takeover source local Button {_fenixSidestickProfileState.TakeoverButtonIndex + 1}: unavailable.";
        }

        var runtime = _fenixSidestickCoordinator.GetRuntimeSnapshot();
        FenixRuntimeStatus =
            $"Runtime: {(runtime.IsMaster ? "Master" : "Slave")}, seat {runtime.Seat}, session {(runtime.SessionActive ? "armed" : "waiting")}, dynamic {(runtime.DynamicAuthorityActive ? "on" : "off")}, bridge {runtime.BridgeControlState}, priority {runtime.PriorityState}, dual {(runtime.DualInputActive ? "on" : "off")}, " +
            $"CAPT PB {(runtime.CaptTakeoverPressed ? "on" : "off")}, FO PB {(runtime.FoTakeoverPressed ? "on" : "off")}, merged pitch {runtime.MergedPitch:0.00}, roll {runtime.MergedRoll:0.00}, " +
            $"lights Cg:{runtime.CaptGreen} Fg:{runtime.FoGreen} Ca:{runtime.CaptRedArrow} Fa:{runtime.FoRedArrow}.";
    }

    private static string DescribePressedButtons(int buttons)
    {
        if (buttons == 0)
            return "none";

        var pressed = new List<string>();
        for (var index = 0; index < 32; index++)
        {
            if (IsConfiguredButtonPressed(buttons, index))
                pressed.Add((index + 1).ToString());
        }

        return pressed.Count == 0 ? "none" : string.Join(", ", pressed);
    }

    private static bool IsConfiguredButtonPressed(int buttons, int buttonIndex) =>
        buttonIndex >= 0 && buttonIndex < 32 && (((uint)buttons >> buttonIndex) & 1u) != 0;

    private void UpdateCurveMarkers(LocalJoystickService.LocalJoystickSample sample)
    {
        UpdateCurveMarker(
            input: sample.XAxis,
            deadzone: _fenixSidestickProfileState.RollDeadzone,
            sensitivity: _fenixSidestickProfileState.RollSensitivity,
            expo: _fenixSidestickProfileState.RollExpo,
            invert: _fenixSidestickProfileState.InvertRoll,
            setLeft: value => FenixRollMarkerLeft = value,
            setTop: value => FenixRollMarkerTop = value);

        UpdateCurveMarker(
            input: -sample.YAxis,
            deadzone: _fenixSidestickProfileState.PitchDeadzone,
            sensitivity: _fenixSidestickProfileState.PitchSensitivity,
            expo: _fenixSidestickProfileState.PitchExpo,
            invert: _fenixSidestickProfileState.InvertPitch,
            setLeft: value => FenixPitchMarkerLeft = value,
            setTop: value => FenixPitchMarkerTop = value);
    }

    private void ResetCurveMarkers()
    {
        FenixRollMarkerLeft = 103;
        FenixRollMarkerTop = 103;
        FenixPitchMarkerLeft = 103;
        FenixPitchMarkerTop = 103;
    }

    private static void UpdateCurveMarker(
        double input,
        double deadzone,
        double sensitivity,
        double expo,
        bool invert,
        Action<double> setLeft,
        Action<double> setTop)
    {
        const double canvasSize = 220.0;
        const double markerSize = 14.0;

        var shapedOutput = ShapeCurveValue(input, deadzone, sensitivity, expo, invert);
        var x = ((Math.Clamp(input, -1.0, 1.0) + 1.0) * 0.5) * canvasSize;
        var y = canvasSize - (((shapedOutput + 1.0) * 0.5) * canvasSize);

        setLeft(Math.Clamp(x - (markerSize / 2.0), 0.0, canvasSize - markerSize));
        setTop(Math.Clamp(y - (markerSize / 2.0), 0.0, canvasSize - markerSize));
    }

    private static Definitions FilterDefinitions(Definitions definitions) =>
        definitions.Name.Equals(FenixAirbusSupport.SharedDefinitionName, StringComparison.OrdinalIgnoreCase)
            ? definitions.Filter(def => !FenixSidestickVars.Contains(def.Get))
            : definitions;

    private static IReadOnlyList<Point> BuildCurvePolylinePoints(double deadzone, double sensitivity, double expo, bool invert)
    {
        const int samples = 41;
        const double canvasSize = 220.0;
        var points = new List<Point>(samples);

        for (var sampleIndex = 0; sampleIndex < samples; sampleIndex++)
        {
            var input = -1.0 + (sampleIndex / (double)(samples - 1)) * 2.0;
            var output = ShapeCurveValue(input, deadzone, sensitivity, expo, invert);
            var x = ((input + 1.0) * 0.5) * canvasSize;
            var y = canvasSize - (((output + 1.0) * 0.5) * canvasSize);
            points.Add(new Point(x, y));
        }

        return points;
    }

    private void ResetFenixAxisSetting(string? setting)
    {
        switch (setting)
        {
            case "roll-sensitivity":
                FenixRollSensitivity = FenixSidestickProfileState.DefaultSensitivity;
                break;
            case "roll-curve":
                FenixRollExpo = FenixSidestickProfileState.DefaultExpo;
                break;
            case "roll-deadzone":
                FenixRollDeadzone = FenixSidestickProfileState.DefaultDeadzone;
                break;
            case "pitch-sensitivity":
                FenixPitchSensitivity = FenixSidestickProfileState.DefaultSensitivity;
                break;
            case "pitch-curve":
                FenixPitchExpo = FenixSidestickProfileState.DefaultExpo;
                break;
            case "pitch-deadzone":
                FenixPitchDeadzone = FenixSidestickProfileState.DefaultDeadzone;
                break;
        }
    }

    private static double ShapeCurveValue(double input, double deadzone, double sensitivity, double expo, bool invert)
    {
        var clampedInput = Math.Clamp(input, -1.0, 1.0);
        var magnitude = Math.Abs(clampedInput);
        var effectiveDeadzone = Math.Clamp(deadzone, 0.0, 0.95);
        if (magnitude <= effectiveDeadzone)
            return 0.0;

        var normalizedMagnitude = (magnitude - effectiveDeadzone) / Math.Max(1e-6, 1.0 - effectiveDeadzone);
        var curvedMagnitude = normalizedMagnitude * (1.0 - expo) + Math.Pow(normalizedMagnitude, 3.0) * expo;
        var shaped = Math.Sign(clampedInput) * Math.Clamp(curvedMagnitude * sensitivity, -1.0, 1.0);
        return invert ? -shaped : shaped;
    }

    private void ApplyGsxSettings(GsxSettings settings)
    {
        _loadingGsxSettings = true;
        try
        {
            GsxSyncEnabled = settings.Enabled;
            GsxPushbackPositionPauseEnabled = settings.PushbackPositionPauseEnabled;
            GsxPushbackFullDesyncEnabled = settings.PushbackFullDesyncEnabled;
            ShowGsxExperimentalNotice = !settings.ExperimentalNoticeAccepted;
        }
        finally
        {
            _loadingGsxSettings = false;
        }
    }

    private void SaveGsxSettings()
    {
        if (_loadingGsxSettings)
            return;

        _gsxSettings.Save(new GsxSettings
        {
            Enabled = GsxSyncEnabled,
            PushbackPositionPauseEnabled = GsxPushbackPositionPauseEnabled,
            PushbackFullDesyncEnabled = GsxPushbackFullDesyncEnabled,
            ExperimentalNoticeAccepted = !ShowGsxExperimentalNotice
        });
    }

    private void DismissGsxExperimentalNotice()
    {
        ShowGsxExperimentalNotice = false;
        SaveGsxSettings();
    }

    private void UpdateHoppieCaptureStatus()
    {
        if (!HoppieEnabled || !HoppieTransparentCaptureEnabled || HoppieRecorderModeEnabled)
        {
            HoppieCaptureStatus = "Hoppie sync is disabled. Enable it here only if you want FS Copilot to manage the Hoppie capture redirect.";
            return;
        }

        if (!HoppieTransparentCaptureManager.IsAdministrator)
        {
            HoppieCaptureStatus = "Hoppie sync is enabled. Restart FS Copilot; the next launch will ask for administrator rights once, then join/leave can update capture without repeated UAC prompts.";
            return;
        }

        if (Connected)
        {
            HoppieCaptureStatus = _hoppieCaptureInstalled
                ? "Automatic capture is active for this session."
                : "Preparing automatic capture for this session.";
            return;
        }

        HoppieCaptureStatus = _hoppieCaptureInstalled
            ? IsBusy
                ? "Preparing automatic capture for this session."
                : "Capture is still active and will turn off when FS Copilot closes."
            : "Automatic capture will switch on when a session starts.";
    }

    private static int NormalizeLocalPort(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            parsed = HoppieSettings.DefaultLocalPort;

        return Math.Clamp(parsed, 1024, 65535);
    }

    [Flags]
    private enum ViewErrors : byte
    {
        None            = 0b_0000_0000,
        Failed          = 0b_0000_0001,
        NotRunning      = 0b_0000_0010,
        NotSupported    = 0b_0000_0100,
        Rejected        = 0b_0000_1000,
        Conflict        = 0b_0001_0000,
        NotLoadedBridge = 0b_0010_0000,
        BridgeMismatch  = 0b_0100_0000
    }

    public record Connection(string PeerId, string Name, int Ping, bool IsDirect, bool HasSeparatorAfter)
    {
        public int QualityLevel
        {
            get
            {
                if (Ping > 800) return 5;
                if (Ping > 400) return 4;
                if (Ping > 200) return 3;
                if (Ping > 100) return 2;
                return 1;
            }
        }
    }

    public sealed record JoystickDeviceOption(int Index, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    public sealed record ButtonOption(int Index, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}

public sealed record HoppieRestartPrompt(string Title, string Message, string Detail)
{
    public static HoppieRestartPrompt ForState(bool enabled) =>
        enabled
            ? new(
                "Restart FS Copilot",
                "Hoppie sync is now enabled.",
                "Close FS Copilot completely and start it again. The next launch will ask for administrator rights once so the Hoppie capture redirect can be installed safely.")
            : new(
                "Restart FS Copilot",
                "Hoppie sync is now disabled.",
                "Close FS Copilot completely and start it again to make sure the local Hoppie proxy and capture redirect are fully stopped.");
}
