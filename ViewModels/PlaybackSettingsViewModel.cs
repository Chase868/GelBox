using System;
using System.Threading;
using System.Threading.Tasks;
using GelBox.Models;
using GelBox.Services;
using Microsoft.Extensions.Logging;

namespace GelBox.ViewModels
{
    /// <summary>
    ///     Handles playback-related settings including quality, audio, subtitles, and playback behavior
    /// </summary>
    public class PlaybackSettingsViewModel : BaseViewModel
    {
        #region Helper Classes

        /// <summary>
        ///     Represents the result of a validation operation
        /// </summary>
        protected class ValidationResult
        {
            public bool IsValid { get; set; }
            public string ErrorMessage { get; set; }
        }

        #endregion

        private readonly IMediaOptimizationService _mediaOptimizationService;
        private readonly IVolumeNormalizationService _volumeNormalizationService;
        private readonly IMusicPlayerService _musicPlayerService;
            private readonly IEqualizerService _equalizerService;
        protected readonly IPreferencesService PreferencesService;

        // Settings state
        private bool _hasUnsavedChanges = false;
        private string _validationError;

        private bool _allowAudioStreamCopy = false;

        // Playback settings
        private bool _autoPlayNextEpisode = true;
        private bool _autoSkipIntros = false;
        private bool _restorePlaybackOnLaunch = true;
        private bool _autoPlayAfterRestoreOnLaunch = false;
        private int _controlsHideDelay = 3;
        // Quality and format settings
        private bool _enableDirectPlay = true;
        private bool _pauseOnFocusLoss = false;
        private string _videoStretchMode = "Uniform";
        // Audio enhancement settings
        private bool _isNightModeEnabled = false;
        private bool _enableVolumeNormalization = true;
        private bool _useAlbumGain = false;
        private double _volumeOffsetDb = 0.0;
        // Appearance settings
        private bool _enableGradientBackground = true;
            // Equalizer settings
            private bool _equalizerEnabled = false;
            private bool _applyEqToVideo = false;
            private double _eqBand0 = 0.0;
            private double _eqBand1 = 0.0;
            private double _eqBand2 = 0.0;
            private double _eqBand3 = 0.0;
            private double _eqBand4 = 0.0;
            private double _eqBand5 = 0.0;
            private string _eqBand0Text = "0.0";
            private string _eqBand1Text = "0.0";
            private string _eqBand2Text = "0.0";
            private string _eqBand3Text = "0.0";
            private string _eqBand4Text = "0.0";
            private string _eqBand5Text = "0.0";
            private bool _suppressEqTextUpdate = false;
            private CancellationTokenSource _eqSaveCts;
            private readonly SemaphoreSlim _eqSaveSemaphore = new SemaphoreSlim(1, 1);
        // Home screen settings
        private bool _showMoviesOnHome = true;
        private bool _showTVShowsOnHome = true;
        private bool _showMusicOnHome = true;

        public PlaybackSettingsViewModel(
            ILogger<PlaybackSettingsViewModel> logger,
            IPreferencesService preferencesService,
            IMediaOptimizationService mediaOptimizationService,
            IVolumeNormalizationService volumeNormalizationService = null,
                IMusicPlayerService musicPlayerService = null,
                IEqualizerService equalizerService = null) : base(logger)
        {
            PreferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
            _mediaOptimizationService = mediaOptimizationService ??
                                        throw new ArgumentNullException(nameof(mediaOptimizationService));
            _volumeNormalizationService = volumeNormalizationService;
            _musicPlayerService = musicPlayerService;
                    _equalizerService = equalizerService;
        }

        #region Properties

        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            protected set => SetProperty(ref _hasUnsavedChanges, value);
        }

        public string ValidationError
        {
            get => _validationError;
            protected set => SetProperty(ref _validationError, value);
        }

        #endregion

        #region Public Methods

        /// <summary>
        ///     Initializes the settings view model by loading current settings
        /// </summary>
        public virtual async Task InitializeAsync()
        {
            // Use the standardized LoadDataAsync pattern
            await LoadDataAsync(true);
        }

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            ValidationError = null;
            await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            await RunOnUIThreadAsync(() => HasUnsavedChanges = false);
        }

        protected override async Task RefreshDataCoreAsync()
        {
            // Refresh just reloads settings
            await LoadSettingsAsync(CancellationToken.None).ConfigureAwait(false);
            await RunOnUIThreadAsync(() => HasUnsavedChanges = false);
        }

        protected override Task ClearDataCoreAsync()
        {
            ValidationError = null;
            HasUnsavedChanges = false;
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Saves the current settings
        /// </summary>
        public virtual async Task<bool> SaveSettingsAsync()
        {
            var context = CreateErrorContext("SaveSettingsAsync", ErrorCategory.Configuration);
            try
            {
                IsLoading = true;
                ValidationError = null;

                // Validate settings before saving
                var validationResult = await ValidateSettingsAsync().ConfigureAwait(false);
                if (!validationResult.IsValid)
                {
                    ValidationError = validationResult.ErrorMessage;
                    return false;
                }

                // Save settings
                await SaveSettingsInternalAsync().ConfigureAwait(false);
                HasUnsavedChanges = false;

                Logger.LogInformation("Settings saved successfully");
                return true;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
                ValidationError = "Failed to save settings. Please try again.";
                return false;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        ///     Resets settings to their default values - async version for consistency
        /// </summary>
        public virtual async Task ResetToDefaultsAsync()
        {
            var context = CreateErrorContext("ResetToDefaultsAsync", ErrorCategory.Configuration);
            try
            {
                IsLoading = true;
                ValidationError = null;

                await ResetToDefaultsInternalAsync().ConfigureAwait(false);
                HasUnsavedChanges = true;

                Logger.LogInformation("Settings reset to defaults");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
                ValidationError = "Failed to reset settings. Please try again.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        ///     Synchronous reset method called by parent SettingsViewModel
        /// </summary>
        public void ResetToDefaults()
        {
            FireAndForget(() => ResetToDefaultsAsync());
        }

        /// <summary>
        ///     Refresh settings
        /// </summary>
        public override async Task RefreshAsync()
        {
            await base.RefreshAsync();
        }

        #endregion

        #region Protected Methods

        /// <summary>
        ///     Loads the settings from storage
        /// </summary>
        protected async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
        {
            // Load all preferences from AppPreferences
            var appPrefs = await PreferencesService.GetAppPreferencesAsync().ConfigureAwait(false);

            await RunOnUIThreadAsync(() =>
            {
                // Playback settings
                _autoPlayNextEpisode = appPrefs.AutoPlayNextEpisode;
                _pauseOnFocusLoss = appPrefs.PauseOnFocusLoss;
                _autoSkipIntros = appPrefs.AutoSkipIntroEnabled;
                _restorePlaybackOnLaunch = appPrefs.RestorePlaybackOnLaunch;
                _autoPlayAfterRestoreOnLaunch = appPrefs.AutoPlayAfterRestoreOnLaunch;
                _controlsHideDelay = appPrefs.ControlsHideDelay;
                _enableDirectPlay = appPrefs.EnableDirectPlay;
                _allowAudioStreamCopy = appPrefs.AllowAudioStreamCopy;
                _videoStretchMode = appPrefs.VideoStretchMode;

                // Audio enhancement settings - night mode is stored separately in MediaOptimizationService
                _isNightModeEnabled = _mediaOptimizationService.GetNightModePreference();
                _enableVolumeNormalization = appPrefs.EnableVolumeNormalization;
                _useAlbumGain = appPrefs.UseAlbumGain;
                _volumeOffsetDb = appPrefs.VolumeOffsetDb;

                    // Equalizer settings
                    _equalizerEnabled = appPrefs.EqualizerEnabled;
                    _applyEqToVideo = appPrefs.ApplyEqToVideo;
                    _eqBand0 = appPrefs.EqBand0Gain;
                    _eqBand1 = appPrefs.EqBand1Gain;
                    _eqBand2 = appPrefs.EqBand2Gain;
                    _eqBand3 = appPrefs.EqBand3Gain;
                    _eqBand4 = appPrefs.EqBand4Gain;
                    _eqBand5 = appPrefs.EqBand5Gain;
                    _eqBand0Text = _eqBand0.ToString("F1");
                    _eqBand1Text = _eqBand1.ToString("F1");
                    _eqBand2Text = _eqBand2.ToString("F1");
                    _eqBand3Text = _eqBand3.ToString("F1");
                    _eqBand4Text = _eqBand4.ToString("F1");
                    _eqBand5Text = _eqBand5.ToString("F1");

                // Appearance settings
                _enableGradientBackground = appPrefs.EnableGradientBackground;

                // Home screen settings
                _showMoviesOnHome = appPrefs.ShowMoviesOnHome;
                _showTVShowsOnHome = appPrefs.ShowTVShowsOnHome;
                _showMusicOnHome = appPrefs.ShowMusicOnHome;

                // Notify all properties changed
                OnPropertyChanged(nameof(AutoPlayNextEpisode));
                OnPropertyChanged(nameof(PauseOnFocusLoss));
                OnPropertyChanged(nameof(AutoSkipIntros));
                OnPropertyChanged(nameof(RestorePlaybackOnLaunch));
                OnPropertyChanged(nameof(AutoPlayAfterRestoreOnLaunch));
                OnPropertyChanged(nameof(ControlsHideDelay));
                OnPropertyChanged(nameof(EnableDirectPlay));
                OnPropertyChanged(nameof(AllowAudioStreamCopy));
                OnPropertyChanged(nameof(VideoStretchMode));
                OnPropertyChanged(nameof(IsNightModeEnabled));
                OnPropertyChanged(nameof(EnableVolumeNormalization));
                OnPropertyChanged(nameof(UseAlbumGain));
                OnPropertyChanged(nameof(VolumeOffsetDb));
                                OnPropertyChanged(nameof(EqualizerEnabled));
                                OnPropertyChanged(nameof(ApplyEqToVideo));
                                OnPropertyChanged(nameof(EqBand0));
                                OnPropertyChanged(nameof(EqBand1));
                                OnPropertyChanged(nameof(EqBand2));
                                OnPropertyChanged(nameof(EqBand3));
                                OnPropertyChanged(nameof(EqBand4));
                                OnPropertyChanged(nameof(EqBand5));
                                OnPropertyChanged(nameof(EqBand0Text));
                                OnPropertyChanged(nameof(EqBand1Text));
                                OnPropertyChanged(nameof(EqBand2Text));
                                OnPropertyChanged(nameof(EqBand3Text));
                                OnPropertyChanged(nameof(EqBand4Text));
                                OnPropertyChanged(nameof(EqBand5Text));
                OnPropertyChanged(nameof(EnableGradientBackground));
                OnPropertyChanged(nameof(ShowMoviesOnHome));
                OnPropertyChanged(nameof(ShowTVShowsOnHome));
                OnPropertyChanged(nameof(ShowMusicOnHome));
            });
        }

        /// <summary>
        ///     Saves the settings to storage
        /// </summary>
        protected async Task SaveSettingsInternalAsync()
        {
            // All settings are saved individually on property change
            // This method could be used to save all settings at once if needed
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Resets settings to their default values
        /// </summary>
        protected async Task ResetToDefaultsInternalAsync()
        {
            // Reset to default values - these should match AppPreferences defaults
            AutoPlayNextEpisode = true;
            PauseOnFocusLoss = true;
            AutoSkipIntros = false;
            RestorePlaybackOnLaunch = true;
            AutoPlayAfterRestoreOnLaunch = false;
            ControlsHideDelay = 3;
            EnableDirectPlay = true;
            AllowAudioStreamCopy = false;
            VideoStretchMode = "Uniform";
            IsNightModeEnabled = false;
            EnableVolumeNormalization = true;
            UseAlbumGain = false;
            VolumeOffsetDb = 0.0;
                        EqualizerEnabled = false;
                        ApplyEqToVideo = false;
                        EqBand0 = 0.0;
                        EqBand1 = 0.0;
                        EqBand2 = 0.0;
                        EqBand3 = 0.0;
                        EqBand4 = 0.0;
                        EqBand5 = 0.0;
            EnableGradientBackground = true;
            ShowMoviesOnHome = true;
            ShowTVShowsOnHome = true;
            ShowMusicOnHome = true;

            await Task.CompletedTask;
        }

        /// <summary>
        ///     Validates the current settings
        /// </summary>
        protected Task<ValidationResult> ValidateSettingsAsync()
        {
            // Validate ControlsHideDelay
            if (ControlsHideDelay < 1 || ControlsHideDelay > 10)
            {
                return Task.FromResult(new ValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Controls hide delay must be between 1 and 10 seconds"
                });
            }

            return Task.FromResult(new ValidationResult { IsValid = true });
        }

        /// <summary>
        ///     Marks that settings have been changed
        /// </summary>
        protected void MarkAsChanged()
        {
            HasUnsavedChanges = true;
        }

        /// <summary>
        ///     Helper method to update a setting value and mark as changed
        /// </summary>
        protected bool SetSettingProperty<T>(ref T storage, T value, string propertyName = null)
        {
            if (SetProperty(ref storage, value, propertyName))
            {
                MarkAsChanged();
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Helper method to safely update app preferences
        /// </summary>
        protected async Task UpdateAppPreferenceAsync(Action<AppPreferences> updateAction, string settingName)
        {
            try
            {
                var appPrefs = await PreferencesService.GetAppPreferencesAsync().ConfigureAwait(false);
                updateAction(appPrefs);
                await PreferencesService.UpdateAppPreferencesAsync(appPrefs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to update {settingName} setting");
                throw;
            }
        }

        #endregion

        #region Playback Settings Properties

        public bool AutoPlayNextEpisode
        {
            get => _autoPlayNextEpisode;
            set
            {
                if (SetSettingProperty(ref _autoPlayNextEpisode, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.AutoPlayNextEpisode = value,
                            nameof(AutoPlayNextEpisode)));
                }
            }
        }

        public bool PauseOnFocusLoss
        {
            get => _pauseOnFocusLoss;
            set
            {
                if (SetSettingProperty(ref _pauseOnFocusLoss, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.PauseOnFocusLoss = value,
                            nameof(PauseOnFocusLoss)));
                }
            }
        }

        public bool AutoSkipIntros
        {
            get => _autoSkipIntros;
            set
            {
                if (SetSettingProperty(ref _autoSkipIntros, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.AutoSkipIntroEnabled = value,
                            nameof(AutoSkipIntros)));
                }
            }
        }

        public bool RestorePlaybackOnLaunch
        {
            get => _restorePlaybackOnLaunch;
            set
            {
                if (SetSettingProperty(ref _restorePlaybackOnLaunch, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.RestorePlaybackOnLaunch = value,
                            nameof(RestorePlaybackOnLaunch)));
                }
            }
        }

        public bool AutoPlayAfterRestoreOnLaunch
        {
            get => _autoPlayAfterRestoreOnLaunch;
            set
            {
                if (SetSettingProperty(ref _autoPlayAfterRestoreOnLaunch, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.AutoPlayAfterRestoreOnLaunch = value,
                            nameof(AutoPlayAfterRestoreOnLaunch)));
                }
            }
        }

        public int ControlsHideDelay
        {
            get => _controlsHideDelay;
            set
            {
                if (SetSettingProperty(ref _controlsHideDelay, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.ControlsHideDelay = value,
                            nameof(ControlsHideDelay)));
                }
            }
        }

        public bool EnableDirectPlay
        {
            get => _enableDirectPlay;
            set
            {
                if (SetSettingProperty(ref _enableDirectPlay, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.EnableDirectPlay = value,
                            nameof(EnableDirectPlay)));
                    // Media optimization service doesn't have InvalidateRecommendations method                    Logger.LogInformation("Direct play setting changed to {Value}", value);
                }
            }
        }

        public bool AllowAudioStreamCopy
        {
            get => _allowAudioStreamCopy;
            set
            {
                if (SetSettingProperty(ref _allowAudioStreamCopy, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.AllowAudioStreamCopy = value,
                            nameof(AllowAudioStreamCopy)));
                    // Media optimization service doesn't have InvalidateRecommendations method                    Logger.LogInformation("Audio stream copy setting changed to {Value}", value);
                }
            }
        }


        public string VideoStretchMode
        {
            get => _videoStretchMode;
            set
            {
                if (SetSettingProperty(ref _videoStretchMode, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.VideoStretchMode = value,
                            nameof(VideoStretchMode)));
                }
            }
        }

        public bool IsNightModeEnabled
        {
            get => _isNightModeEnabled;
            set
            {
                if (SetSettingProperty(ref _isNightModeEnabled, value))
                {
                    // Since AppPreferences doesn't have this property yet, just log it
                    Logger.LogInformation("Night mode setting changed to {Value}", value);

                    // Call MediaOptimizationService if it has night mode methods
                    _mediaOptimizationService?.SetNightMode(value);
                }
            }
        }

        public bool EnableVolumeNormalization
        {
            get => _enableVolumeNormalization;
            set
            {
                if (SetSettingProperty(ref _enableVolumeNormalization, value))
                {
                    FireAndForget(async () =>
                    {
                        await UpdateAppPreferenceAsync(prefs => prefs.EnableVolumeNormalization = value,
                            nameof(EnableVolumeNormalization));
                        await ReapplyNormalizationAsync();
                    });
                }
            }
        }

        public bool UseAlbumGain
        {
            get => _useAlbumGain;
            set
            {
                if (SetSettingProperty(ref _useAlbumGain, value))
                {
                    FireAndForget(async () =>
                    {
                        await UpdateAppPreferenceAsync(prefs => prefs.UseAlbumGain = value,
                            nameof(UseAlbumGain));
                        await ReapplyNormalizationAsync();
                    });
                }
            }
        }

        public double VolumeOffsetDb
        {
            get => _volumeOffsetDb;
            set
            {
                if (SetSettingProperty(ref _volumeOffsetDb, Math.Clamp(value, -10.0, 10.0)))
                {
                    FireAndForget(async () =>
                    {
                        await UpdateAppPreferenceAsync(prefs => prefs.VolumeOffsetDb = _volumeOffsetDb,
                            nameof(VolumeOffsetDb));
                        await ReapplyNormalizationAsync();
                    });
                }
            }
        }

        /// <summary>
        /// Clears the normalization cache and re-applies volume normalization to the currently
        /// playing track so preference changes take effect without restarting playback.
        /// </summary>
        private async Task ReapplyNormalizationAsync()
        {
            if (_volumeNormalizationService == null || _musicPlayerService == null)
            {
                return;
            }

            var currentItem = _musicPlayerService.CurrentItem;
            var player = _musicPlayerService.MediaPlayer;
            if (currentItem == null || player == null)
            {
                return;
            }

            // Evict stale cache entries so the updated preference is used
            _volumeNormalizationService.ClearCache();

            await _volumeNormalizationService.ApplyVolumeNormalizationAsync(player, currentItem)
                .ConfigureAwait(false);

            Logger.LogInformation("Re-applied volume normalization after preference change");
        }

        #endregion



        #region Equalizer Properties

        public bool EqualizerEnabled
        {
            get => _equalizerEnabled;
            set
            {
                if (SetSettingProperty(ref _equalizerEnabled, value))
                {
                    _equalizerService?.SetEnabled(value);
                    FireAndForget(() => UpdateAppPreferenceAsync(prefs => prefs.EqualizerEnabled = value, nameof(EqualizerEnabled)));
                }
            }
        }

        public bool ApplyEqToVideo
        {
            get => _applyEqToVideo;
            set
            {
                if (SetSettingProperty(ref _applyEqToVideo, value))
                {
                    _equalizerService?.SetEqForVideoEnabled(value);
                    FireAndForget(() => UpdateAppPreferenceAsync(prefs => prefs.ApplyEqToVideo = value, nameof(ApplyEqToVideo)));
                }
            }
        }

        public double EqBand0
        {
            get => _eqBand0;
            set
            {
                double clamped = Math.Clamp(value, -12.0, 12.0);
                if (SetSettingProperty(ref _eqBand0, clamped))
                {
                    _equalizerService?.SetBandGain(0, clamped);
                    FireAndForget(ScheduleEqSaveAsync);
                    if (!_suppressEqTextUpdate) { _eqBand0Text = clamped.ToString("F1"); OnPropertyChanged(nameof(EqBand0Text)); }
                }
            }
        }

        public string EqBand0Text
        {
            get => _eqBand0Text;
            set
            {
                if (_eqBand0Text == value) return;
                _eqBand0Text = value;
                OnPropertyChanged();
                if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                { _suppressEqTextUpdate = true; EqBand0 = parsed; _suppressEqTextUpdate = false; }
            }
        }

        public double EqBand1
        {
            get => _eqBand1;
            set
            {
                double clamped = Math.Clamp(value, -12.0, 12.0);
                if (SetSettingProperty(ref _eqBand1, clamped))
                {
                    _equalizerService?.SetBandGain(1, clamped);
                    FireAndForget(ScheduleEqSaveAsync);
                    if (!_suppressEqTextUpdate) { _eqBand1Text = clamped.ToString("F1"); OnPropertyChanged(nameof(EqBand1Text)); }
                }
            }
        }

        public string EqBand1Text
        {
            get => _eqBand1Text;
            set
            {
                if (_eqBand1Text == value) return;
                _eqBand1Text = value;
                OnPropertyChanged();
                if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                { _suppressEqTextUpdate = true; EqBand1 = parsed; _suppressEqTextUpdate = false; }
            }
        }

        public double EqBand2
        {
            get => _eqBand2;
            set
            {
                double clamped = Math.Clamp(value, -12.0, 12.0);
                if (SetSettingProperty(ref _eqBand2, clamped))
                {
                    _equalizerService?.SetBandGain(2, clamped);
                    FireAndForget(ScheduleEqSaveAsync);
                    if (!_suppressEqTextUpdate) { _eqBand2Text = clamped.ToString("F1"); OnPropertyChanged(nameof(EqBand2Text)); }
                }
            }
        }

        public string EqBand2Text
        {
            get => _eqBand2Text;
            set
            {
                if (_eqBand2Text == value) return;
                _eqBand2Text = value;
                OnPropertyChanged();
                if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                { _suppressEqTextUpdate = true; EqBand2 = parsed; _suppressEqTextUpdate = false; }
            }
        }

        public double EqBand3
        {
            get => _eqBand3;
            set
            {
                double clamped = Math.Clamp(value, -12.0, 12.0);
                if (SetSettingProperty(ref _eqBand3, clamped))
                {
                    _equalizerService?.SetBandGain(3, clamped);
                    FireAndForget(ScheduleEqSaveAsync);
                    if (!_suppressEqTextUpdate) { _eqBand3Text = clamped.ToString("F1"); OnPropertyChanged(nameof(EqBand3Text)); }
                }
            }
        }

        public string EqBand3Text
        {
            get => _eqBand3Text;
            set
            {
                if (_eqBand3Text == value) return;
                _eqBand3Text = value;
                OnPropertyChanged();
                if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                { _suppressEqTextUpdate = true; EqBand3 = parsed; _suppressEqTextUpdate = false; }
            }
        }

        public double EqBand4
        {
            get => _eqBand4;
            set
            {
                double clamped = Math.Clamp(value, -12.0, 12.0);
                if (SetSettingProperty(ref _eqBand4, clamped))
                {
                    _equalizerService?.SetBandGain(4, clamped);
                    FireAndForget(ScheduleEqSaveAsync);
                    if (!_suppressEqTextUpdate) { _eqBand4Text = clamped.ToString("F1"); OnPropertyChanged(nameof(EqBand4Text)); }
                }
            }
        }

        public string EqBand4Text
        {
            get => _eqBand4Text;
            set
            {
                if (_eqBand4Text == value) return;
                _eqBand4Text = value;
                OnPropertyChanged();
                if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                { _suppressEqTextUpdate = true; EqBand4 = parsed; _suppressEqTextUpdate = false; }
            }
        }

        public double EqBand5
        {
            get => _eqBand5;
            set
            {
                double clamped = Math.Clamp(value, -12.0, 12.0);
                if (SetSettingProperty(ref _eqBand5, clamped))
                {
                    _equalizerService?.SetBandGain(5, clamped);
                    FireAndForget(ScheduleEqSaveAsync);
                    if (!_suppressEqTextUpdate) { _eqBand5Text = clamped.ToString("F1"); OnPropertyChanged(nameof(EqBand5Text)); }
                }
            }
        }

        public string EqBand5Text
        {
            get => _eqBand5Text;
            set
            {
                if (_eqBand5Text == value) return;
                _eqBand5Text = value;
                OnPropertyChanged();
                if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                { _suppressEqTextUpdate = true; EqBand5 = parsed; _suppressEqTextUpdate = false; }
            }
        }

        private async Task ScheduleEqSaveAsync()
        {
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _eqSaveCts, newCts);
            oldCts?.Cancel();
            oldCts?.Dispose();
            try
            {
                await Task.Delay(500, newCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _eqSaveSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (newCts.IsCancellationRequested)
                {
                    return;
                }

                await UpdateAppPreferenceAsync(prefs =>
                {
                    prefs.EqBand0Gain = _eqBand0;
                    prefs.EqBand1Gain = _eqBand1;
                    prefs.EqBand2Gain = _eqBand2;
                    prefs.EqBand3Gain = _eqBand3;
                    prefs.EqBand4Gain = _eqBand4;
                    prefs.EqBand5Gain = _eqBand5;
                }, "EqBands").ConfigureAwait(false);
            }
            finally
            {
                _eqSaveSemaphore.Release();
            }
        }

        /// <summary>Resets all EQ bands to 0 dB (flat).</summary>
        public void ResetEqualizer()
        {
            EqBand0 = 0.0;
            EqBand1 = 0.0;
            EqBand2 = 0.0;
            EqBand3 = 0.0;
            EqBand4 = 0.0;
            EqBand5 = 0.0;
        }

        /// <summary>Event handler overload for XAML x:Bind button click.</summary>
        public void ResetEqualizerClick(object sender, Windows.UI.Xaml.RoutedEventArgs e) => ResetEqualizer();

        #endregion

        #region Appearance Settings Properties

        public bool EnableGradientBackground
        {
            get => _enableGradientBackground;
            set
            {
                if (SetSettingProperty(ref _enableGradientBackground, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.EnableGradientBackground = value,
                            nameof(EnableGradientBackground)));
                }
            }
        }

        #endregion

        #region Home Screen Settings Properties

        public bool ShowMoviesOnHome
        {
            get => _showMoviesOnHome;
            set
            {
                if (SetSettingProperty(ref _showMoviesOnHome, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.ShowMoviesOnHome = value,
                            nameof(ShowMoviesOnHome)));
                }
            }
        }

        public bool ShowTVShowsOnHome
        {
            get => _showTVShowsOnHome;
            set
            {
                if (SetSettingProperty(ref _showTVShowsOnHome, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.ShowTVShowsOnHome = value,
                            nameof(ShowTVShowsOnHome)));
                }
            }
        }

        public bool ShowMusicOnHome
        {
            get => _showMusicOnHome;
            set
            {
                if (SetSettingProperty(ref _showMusicOnHome, value))
                {
                    FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.ShowMusicOnHome = value,
                            nameof(ShowMusicOnHome)));
                }
            }
        }

        #endregion
    }
}