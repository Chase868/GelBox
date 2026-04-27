using System;
using System.Collections.Generic;
using System.Linq;
using GelBox.Constants;
using GelBox.Helpers;
using GelBox.Models;
using GelBox.Services;
using GelBox.Views;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using RepeatMode = GelBox.Services.RepeatMode;

namespace GelBox.Controls
{
    public sealed partial class MusicPlayer : BaseControl
    {
        private MediaPlayer _currentMediaPlayer;
        private bool _isUpdatingProgress = false;
        private IMusicPlayerService _musicPlayerService;
        private INavigationService _navigationService;
        private JellyfinApiClient _apiClient;
        private IUserProfileService _userProfileService;
        private IImageLoadingService _imageLoadingService;
        private IVolumeNormalizationService _volumeNormalizationService;
        private IEqualizerService _equalizerService;
        private DispatcherTimer _progressTimer;
        private bool _isQueueOpen = false;
        private bool _isHistoryExpanded = false;

        public MusicPlayer()
        {
            InitializeComponent(); Loaded += MusicPlayer_Loaded;
            Unloaded += MusicPlayer_Unloaded;
        }

        protected override void OnServicesInitialized(IServiceProvider services)
        {
            // Store service provider for later use
            _musicPlayerService = GetService<IMusicPlayerService>();
            _navigationService = GetService<INavigationService>();
            _apiClient = GetService<JellyfinApiClient>();
            _userProfileService = GetService<IUserProfileService>();
            _imageLoadingService = GetService<IImageLoadingService>();
            _volumeNormalizationService = GetService<IVolumeNormalizationService>();
            _equalizerService = GetService<IEqualizerService>();
        }

        private void MusicPlayer_Loaded(object sender, RoutedEventArgs e)
        {
            var context = CreateErrorContext("MusicPlayerLoad");
            try
            {
                // Wire up click handlers programmatically to avoid XAML parsing issues on Xbox
                if (PreviousButton != null)
                {
                    PreviousButton.Click += PreviousButton_Click;
                }

                if (RewindButton != null)
                {
                    RewindButton.Click += RewindButton_Click;
                }

                if (PlayPauseButton != null)
                {
                    PlayPauseButton.Click += PlayPauseButton_Click;
                }

                if (FastForwardButton != null)
                {
                    FastForwardButton.Click += FastForwardButton_Click;
                }

                if (NextButton != null)
                {
                    NextButton.Click += NextButton_Click;
                }

                if (MenuButton != null)
                {
                    MenuButton.Click += MenuButton_Click;
                }

                if (ShuffleButton != null)
                {
                    ShuffleButton.Click += ShuffleButton_Click;
                }

                if (QueueToggleButton != null)
                {
                    QueueToggleButton.Click += QueueToggleButton_Click;
                }

                if (CloseQueueButton != null)
                {
                    CloseQueueButton.Click += CloseQueueButton_Click;
                }

                if (HistoryToggleButton != null)
                {
                    HistoryToggleButton.Click += HistoryToggleButton_Click;
                }

                // Subscribe to service events
                if (_musicPlayerService != null)
                {
                    _musicPlayerService.NowPlayingChanged += OnNowPlayingChanged;
                    _musicPlayerService.PlaybackStateChanged += OnPlaybackStateChanged;
                    _musicPlayerService.ShuffleStateChanged += OnShuffleStateChanged;
                    _musicPlayerService.RepeatModeChanged += OnRepeatModeChanged;
                    _musicPlayerService.QueueChanged += OnQueueChanged;

                    // Subscribe to MediaPlayer events for duration updates
                    SubscribeToMediaPlayer();
                }
                _progressTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(UIConstants.MINI_PLAYER_UPDATE_INTERVAL_MS)
                };
                _progressTimer.Tick += ProgressTimer_Tick;


                // Check if there's already something playing
                if (_musicPlayerService?.CurrentItem != null)
                {
                    UpdateNowPlayingInfo(_musicPlayerService.CurrentItem);
                    UpdatePlayPauseButton();
                    if (_musicPlayerService.IsPlaying)
                    {
                        _progressTimer.Start();
                    }

                    // Update shuffle and repeat button states
                    UpdateShuffleButton();
                    UpdateRepeatButton();
                    UpdateNavigationButtons();
                }
                else
                {
                    // No content playing, ensure we're hidden
                    Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }
            }
        }

        private void MusicPlayer_Unloaded(object sender, RoutedEventArgs e)
        {
            var context = CreateErrorContext("MusicPlayerUnload", ErrorCategory.User, ErrorSeverity.Warning);
            try
            {
                // Unwire click handlers
                if (PreviousButton != null)
                {
                    PreviousButton.Click -= PreviousButton_Click;
                }

                if (RewindButton != null)
                {
                    RewindButton.Click -= RewindButton_Click;
                }

                if (PlayPauseButton != null)
                {
                    PlayPauseButton.Click -= PlayPauseButton_Click;
                }

                if (FastForwardButton != null)
                {
                    FastForwardButton.Click -= FastForwardButton_Click;
                }

                if (NextButton != null)
                {
                    NextButton.Click -= NextButton_Click;
                }

                if (MenuButton != null)
                {
                    MenuButton.Click -= MenuButton_Click;
                }

                if (ShuffleButton != null)
                {
                    ShuffleButton.Click -= ShuffleButton_Click;
                }

                if (QueueToggleButton != null)
                {
                    QueueToggleButton.Click -= QueueToggleButton_Click;
                }

                if (CloseQueueButton != null)
                {
                    CloseQueueButton.Click -= CloseQueueButton_Click;
                }

                if (HistoryToggleButton != null)
                {
                    HistoryToggleButton.Click -= HistoryToggleButton_Click;
                }

                // Stop and dispose timers
                if (_progressTimer != null)
                {
                    _progressTimer.Stop();
                    _progressTimer.Tick -= ProgressTimer_Tick;
                    _progressTimer = null;
                }


                // Unsubscribe from events
                if (_musicPlayerService != null)
                {
                    _musicPlayerService.NowPlayingChanged -= OnNowPlayingChanged;
                    _musicPlayerService.PlaybackStateChanged -= OnPlaybackStateChanged;
                    _musicPlayerService.ShuffleStateChanged -= OnShuffleStateChanged;
                    _musicPlayerService.RepeatModeChanged -= OnRepeatModeChanged;
                    _musicPlayerService.QueueChanged -= OnQueueChanged;
                }

                // Unsubscribe from MediaPlayer events
                UnsubscribeFromMediaPlayer();

                Logger?.LogInformation("MusicPlayer unloaded and cleaned up");
            }
            catch (Exception ex)
            {
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }
            }
        }

        private void OnNowPlayingChanged(object sender, BaseItemDto item)
        {
            Logger?.LogInformation($"MusicPlayer received NowPlayingChanged event for: {item?.Name}");
#if DEBUG
            Logger?.LogDebug($"MusicPlayer: NowPlayingChanged event received for: {item?.Name}");
#endif
            AsyncHelper.FireAndForget(async () =>
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    var context = CreateErrorContext("NowPlayingChanged");
                    try
                    {
#if DEBUG
                        Logger?.LogDebug($"MusicPlayer: Updating UI for: {item?.Name}");
#endif
                        UpdateNowPlayingInfo(item);
                        UpdateNavigationButtons();
                        if (_isQueueOpen)
                        {
                            RebuildQueuePanel();
                        }

                        // Re-subscribe to MediaPlayer in case it changed
                        SubscribeToMediaPlayer();
                    }
                    catch (Exception ex)
                    {
                        if (ErrorHandler is ErrorHandlingService errorService)
                        {
                            errorService.HandleError(ex, context);
                        }
                        else
                        {
                            AsyncHelper.FireAndForget(async () =>
                                await ErrorHandler.HandleErrorAsync(ex, context, false));
                        }
                    }
                }, Dispatcher, Logger);
            }, Logger, typeof(MusicPlayer));
        }

        private void OnPlaybackStateChanged(object sender, MediaPlaybackState state)
        {
            AsyncHelper.FireAndForget(async () =>
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    try
                    {
                        UpdatePlayPauseButton();

                        if (state == MediaPlaybackState.Playing)
                        {
                            _progressTimer.Start();
                        }
                        else
                        {
                            _progressTimer.Stop();
                        }
                    }
                    catch (Exception ex)
                    {
                        var context = CreateErrorContext("PlaybackStateChanged");
                        if (ErrorHandler is ErrorHandlingService errorService)
                        {
                            errorService.HandleError(ex, context);
                        }
                        else
                        {
                            AsyncHelper.FireAndForget(async () =>
                                await ErrorHandler.HandleErrorAsync(ex, context, false));
                        }
                    }
                }, Dispatcher, Logger);
            }, Logger, typeof(MusicPlayer));
        }

        private void OnShuffleStateChanged(object sender, bool isShuffled)
        {
            Logger?.LogInformation($"MusicPlayer received ShuffleStateChanged event: {isShuffled}");
            AsyncHelper.FireAndForget(async () =>
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    try
                    {
                        UpdateShuffleButton();
                    }
                    catch (Exception ex)
                    {
                        var context = CreateErrorContext("ShuffleStateChanged");
                        if (ErrorHandler is ErrorHandlingService errorService)
                        {
                            errorService.HandleError(ex, context);
                        }
                        else
                        {
                            AsyncHelper.FireAndForget(async () =>
                                await ErrorHandler.HandleErrorAsync(ex, context, false));
                        }
                    }
                }, Dispatcher, Logger);
            }, Logger, typeof(MusicPlayer));
        }

        private void OnRepeatModeChanged(object sender, RepeatMode repeatMode)
        {
            Logger?.LogInformation($"MusicPlayer received RepeatModeChanged event: {repeatMode}");
            AsyncHelper.FireAndForget(async () =>
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    try
                    {
                        UpdateRepeatButton();
                    }
                    catch (Exception ex)
                    {
                        var context = CreateErrorContext("RepeatModeChanged");
                        if (ErrorHandler is ErrorHandlingService errorService)
                        {
                            errorService.HandleError(ex, context);
                        }
                        else
                        {
                            AsyncHelper.FireAndForget(async () =>
                                await ErrorHandler.HandleErrorAsync(ex, context, false));
                        }
                    }
                }, Dispatcher, Logger);
            }, Logger, typeof(MusicPlayer));
        }

        private void OnQueueChanged(object sender, List<BaseItemDto> queue)
        {
            Logger?.LogInformation($"MusicPlayer received QueueChanged event: {queue?.Count ?? 0} items");
            AsyncHelper.FireAndForget(async () =>
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    try
                    {
                        UpdateNavigationButtons();
                        if (_isQueueOpen)
                        {
                            RebuildQueuePanel();
                        }
                    }
                    catch (Exception ex)
                    {
                        var context = CreateErrorContext("QueueChanged");
                        if (ErrorHandler is ErrorHandlingService errorService)
                        {
                            errorService.HandleError(ex, context);
                        }
                        else
                        {
                            AsyncHelper.FireAndForget(async () =>
                                await ErrorHandler.HandleErrorAsync(ex, context, false));
                        }
                    }
                }, Dispatcher, Logger);
            }, Logger, typeof(MusicPlayer));
        }

        private void UpdateNowPlayingInfo(BaseItemDto item)
        {
#if DEBUG
            Logger?.LogDebug($"MusicPlayer: UpdateNowPlayingInfo called for: {item?.Name}");
#endif
            if (item == null)
            {
#if DEBUG
                Logger?.LogDebug("MusicPlayer: Item is null, hiding MusicPlayer");
#endif
                Visibility = Visibility.Collapsed;
                return;
            }

            // Show the MusicPlayer when we have content
#if DEBUG
            Logger?.LogDebug("MusicPlayer: Setting visibility to Visible");
#endif
            Visibility = Visibility.Visible;

            // Update UI with null checks
            if (TrackName != null)
            {
                TrackName.Text = item.Name ?? "Unknown Track";
            }

            if (ArtistName != null)
            {
                ArtistName.Text = item.AlbumArtist ?? item.Artists?.FirstOrDefault() ?? "Unknown Artist";
            }


            // Update duration from metadata if available
            if (TotalTimeText != null && item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0)
            {
                var duration = TimeSpan.FromTicks(item.RunTimeTicks.Value);
                TotalTimeText.Text = TimeFormattingHelper.FormatTime(duration);
                Logger?.LogDebug($"MusicPlayer: Set duration from metadata: {TimeFormattingHelper.FormatTime(duration)}");
            }

            // Load album art
            if (AlbumArt != null)
            {
                // Load album art asynchronously
                AsyncHelper.FireAndForget(async () =>
                {
                    if (_imageLoadingService != null)
                    {
                        BaseItemDto imageItem = null;

                        // Determine which item to use for the image
                        if (item.AlbumId.HasValue)
                        {
                            // Create a temporary item for the album
                            imageItem = new BaseItemDto { Id = item.AlbumId };
                        }
                        else if (ImageHelper.HasImageType(item, "Primary"))
                        {
                            imageItem = item;
                        }

                        if (imageItem != null)
                        {
                            await _imageLoadingService.LoadImageIntoTargetAsync(
                                imageItem,
                                "Primary",
                                imageSource => AlbumArt.Source = imageSource,
                                Dispatcher,
                                200,
                                200
                            ).ConfigureAwait(false);
                        }
                    }
                }, Logger, typeof(MusicPlayer));
            }

            // Reset progress with null checks
            if (ProgressBar != null)
            {
                ProgressBar.Width = 0;
            }

            if (CurrentTimeText != null)
            {
                CurrentTimeText.Text = "0:00";
            }
            // Don't reset TotalTimeText here - it's already set from metadata above

            Visibility = Visibility.Visible;
        }

        private void ProgressTimer_Tick(object sender, object e)
        {
            if (!_isUpdatingProgress && _musicPlayerService?.MediaPlayer?.PlaybackSession != null)
            {
                _isUpdatingProgress = true;
                try
                {
                    var playbackSession = _musicPlayerService.MediaPlayer.PlaybackSession;
                    if (playbackSession == null)
                    {
                        return;
                    }

                    var position = playbackSession.Position;

                    // Always use metadata duration for consistency
                    var currentItem = _musicPlayerService?.CurrentItem;
                    if (currentItem?.RunTimeTicks.HasValue == true && currentItem.RunTimeTicks.Value > 0)
                    {
                        var duration = TimeSpan.FromTicks(currentItem.RunTimeTicks.Value);

                        // Update progress bar width as percentage
                        var progressPercentage = position.TotalSeconds / duration.TotalSeconds;
                        var progressBarContainer = ProgressBar?.Parent as Grid;
                        if (progressBarContainer != null && ProgressBar != null)
                        {
                            ProgressBar.Width = progressBarContainer.ActualWidth * progressPercentage;
                        }

                        // Update time displays
                        if (CurrentTimeText != null)
                        {
                            CurrentTimeText.Text = TimeFormattingHelper.FormatTime(position);
                        }
                        // Duration text is already set from metadata in UpdateNowPlayingInfo
                    }
                }
#if DEBUG
                catch (Exception ex)
                {
                    Logger?.LogDebug($"MusicPlayer: Error in ProgressTimer_Tick - {ex.Message}");
                }
#else
                catch
                {
                }
#endif
                finally
                {
                    _isUpdatingProgress = false;
                }
            }
        }


        private void UpdatePlayPauseButton()
        {
            if (PlayPauseIcon == null)
            {
                return;
            }

            var isPlaying = _musicPlayerService?.IsPlaying == true;
            PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768"; // Pause : Play
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_musicPlayerService?.IsPlaying == true)
                {
                    _musicPlayerService.Pause();
                }
                else
                {
                    _musicPlayerService?.Play();
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in PlayPauseButton_Click");
            }
        }

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.SkipPrevious();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in PreviousButton_Click");
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.SkipNext();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in NextButton_Click");
            }
        }

        private void RewindButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.SeekBackward(10);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in RewindButton_Click");
            }
        }

        private void FastForwardButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.SeekForward(30);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in FastForwardButton_Click");
            }
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            // Flyout will open automatically
            // No direct service calls here that are likely to throw unhandled.
        }

        private void GoToArtist_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var currentItem = _musicPlayerService?.CurrentItem;
                if (currentItem?.AlbumArtists?.Any() == true || currentItem?.ArtistItems?.Any() == true)
                {
                    var artistItem = currentItem.ArtistItems?.FirstOrDefault() ??
                                     currentItem.AlbumArtists?.FirstOrDefault();
                    if (artistItem != null && _navigationService != null) // Added null check for navigationService
                    {
                        _navigationService.Navigate(typeof(ArtistDetailsPage), artistItem.Id.Value.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in GoToArtist_Click");
            }
        }

        private void GoToAlbum_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var currentItem = _musicPlayerService?.CurrentItem;
                if (currentItem?.AlbumId.HasValue == true)
                {
                    if (_navigationService != null) // Added null check for navigationService
                    {
                        _navigationService.Navigate(typeof(AlbumDetailsPage), currentItem.AlbumId.Value.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in GoToAlbum_Click");
            }
        }

        private async void StartInstantMix_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var currentItem = _musicPlayerService?.CurrentItem;
                if (currentItem?.Id != null)
                {
                    Logger?.LogInformation($"Starting instant mix for '{currentItem.Name}'");

                    // Get the API client
                    if (_apiClient != null && _userProfileService != null)
                    {
                        var userId = _userProfileService.CurrentUserId;
                        if (!string.IsNullOrEmpty(userId) && Guid.TryParse(userId, out var userIdGuid))
                        {
                            // Get instant mix for the current track
                            var instantMix = await _apiClient.Items[currentItem.Id.Value].InstantMix.GetAsync(config =>
                            {
                                config.QueryParameters.UserId = userIdGuid;
                                config.QueryParameters.Limit = MediaConstants.MAX_DISCOVERY_QUERY_LIMIT;
                            });

                            if (instantMix?.Items?.Any() == true)
                            {
                                // Play the instant mix
                                await _musicPlayerService.PlayItems(instantMix.Items.ToList());
                                Logger?.LogInformation($"Started instant mix with {instantMix.Items.Count} tracks");
                            }
                            else
                            {
                                Logger?.LogWarning("No items returned for instant mix");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in StartInstantMix_Click");
            }
        }


        private void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.ClearQueue();
                Logger?.LogInformation("Queue cleared");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ClearQueue_Click");
            }
        }

        private async void PlaybackInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var item = _musicPlayerService?.CurrentItem;
                var mediaSource = _musicPlayerService?.CurrentMediaSourceInfo;

                // --- Source audio stream details ---
                var audioStream = mediaSource?.MediaStreams?.FirstOrDefault(
                    s => s.Type == MediaStream_Type.Audio);

                string container = mediaSource?.Container?.ToUpperInvariant() ?? "Unknown";
                string audioCodec = audioStream?.Codec?.ToUpperInvariant() ?? mediaSource?.Container?.ToUpperInvariant() ?? "Unknown";

                string sourceBitrate = mediaSource?.Bitrate.HasValue == true
                    ? $"{mediaSource.Bitrate.Value / 1000:N0} kbps"
                    : (audioStream?.BitRate.HasValue == true ? $"{audioStream.BitRate.Value / 1000:N0} kbps" : "Unknown");

                string sampleRate = audioStream?.SampleRate.HasValue == true
                    ? $"{audioStream.SampleRate.Value:N0} Hz"
                    : "Unknown";

                string bitDepth = audioStream?.BitDepth.HasValue == true
                    ? $"{audioStream.BitDepth.Value}-bit"
                    : null;

                string channels = audioStream?.Channels.HasValue == true
                    ? FormatChannels(audioStream.Channels.Value)
                    : null;

                // --- Transcoding ---
                bool isTranscoded = _musicPlayerService?.IsCurrentlyTranscoded == true;
                string transcodedContainer = _musicPlayerService?.TranscodedContainer?.ToUpperInvariant();
                int? transcodedMaxKbps = _musicPlayerService?.TranscodedMaxBitrateKbps;

                // --- Normalization ---
                NormalizationDetails normDetails = null;
                if (_volumeNormalizationService != null && item != null)
                {
                    normDetails = await _volumeNormalizationService.GetNormalizationDetailsAsync(item);
                }

                // --- Build dialog content ---
                var content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 600
                };

                var outerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
                var leftStack = new StackPanel { Spacing = 4, Width = 240 };
                var rightStack = new StackPanel { Spacing = 4, Width = 300 };
                outerPanel.Children.Add(leftStack);
                outerPanel.Children.Add(rightStack);
                content.Content = outerPanel;

                // --- Left column: Source Audio + Playback Method ---
                AddSectionHeader(leftStack, "Source Audio");
                AddRow(leftStack, "Container", container);
                if (audioCodec != container)
                    AddRow(leftStack, "Codec", audioCodec);
                AddRow(leftStack, "Bitrate", sourceBitrate);
                AddRow(leftStack, "Sample Rate", sampleRate);
                if (bitDepth != null)
                    AddRow(leftStack, "Bit Depth", bitDepth);
                if (channels != null)
                    AddRow(leftStack, "Channels", channels);

                AddSectionHeader(leftStack, "Playback Method");
                if (isTranscoded)
                {
                    string transLabel = "Transcoded";
                    if (!string.IsNullOrEmpty(transcodedContainer))
                        transLabel += $" \u2192 {transcodedContainer}";
                    AddRow(leftStack, "Method", transLabel);
                    if (transcodedMaxKbps.HasValue)
                        AddRow(leftStack, "Max bitrate", $"{transcodedMaxKbps.Value:N0} kbps");
                }
                else
                {
                    AddRow(leftStack, "Method", "Direct Stream");
                }

                // --- Right column: Volume Normalization + Equalizer ---
                AddSectionHeader(rightStack, "Volume Normalization");
                if (normDetails == null)
                {
                    AddRow(rightStack, "Status", "Unavailable");
                }
                else
                {
                    AddRow(rightStack, "Status", normDetails.IsEnabled ? "Enabled" : "Disabled");

                    if (normDetails.IsEnabled)
                    {
                        AddRow(rightStack, "Source", normDetails.UseAlbumGain ? "Album Gain" : "Track Gain");

                        double? displayGainDb = normDetails.UseAlbumGain
                            ? normDetails.AlbumGainDb
                            : normDetails.TrackGainDb;

                        AddRow(rightStack, "Stored Gain",
                            displayGainDb.HasValue
                                ? $"{displayGainDb.Value:+0.00;-0.00} dB"
                                : "Not available");

                        AddRow(rightStack, "Volume Offset",
                            normDetails.VolumeOffsetDb == 0.0
                                ? "0 dB (none)"
                                : $"{normDetails.VolumeOffsetDb:+0.0;-0.0} dB");

                        if (normDetails.AppliedGainDb.HasValue)
                            AddRow(rightStack, "Applied Adj.", $"{normDetails.AppliedGainDb.Value:+0.0;-0.0} dB");
                        else
                            AddRow(rightStack, "Applied Adj.", "None (no data)");
                    }
                }

                AddSectionHeader(rightStack, "Equalizer");
                if (_equalizerService == null)
                {
                    AddRow(rightStack, "Status", "Unavailable");
                }
                else
                {
                    bool eqEnabled = _equalizerService.IsEnabled;
                    string eqStatus = eqEnabled ? "Enabled" : "Disabled";
                    if (eqEnabled && _equalizerService.IsEqForVideoEnabled)
                        eqStatus += ", Video";
                    AddRow(rightStack, "Status", eqStatus);

                    if (eqEnabled)
                    {
                        static string G(double v) => $"{v:+0.0;-0.0;0.0} dB";
                        double[] gains = new double[6];
                        for (int i = 0; i < 6; i++) gains[i] = _equalizerService.GetBandGain(i);

                        AddRow(rightStack, "Low bands",
                            $"60Hz {G(gains[0])}\n180Hz {G(gains[1])}\n500Hz {G(gains[2])}");
                        AddRow(rightStack, "High bands",
                            $"1.4kHz {G(gains[3])}\n4kHz {G(gains[4])}\n11kHz {G(gains[5])}");
                    }
                }

                var dialog = new ContentDialog
                {
                    Title = "Playback Information",
                    Content = content,
                    CloseButtonText = "Close"
                };

                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in PlaybackInfo_Click");
            }
        }

        private static string FormatChannels(int count)
        {
            return count switch
            {
                1 => "1 (Mono)",
                2 => "2 (Stereo)",
                6 => "6 (5.1)",
                8 => "8 (7.1)",
                _ => count.ToString()
            };
        }

        private static void AddSectionHeader(StackPanel parent, string title)
        {
            parent.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                Margin = new Windows.UI.Xaml.Thickness(0, 12, 0, 4)
            });
        }

        private static void AddRow(StackPanel parent, string label, string value)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelBlock = new TextBlock
            {
                Text = label + ":",
                FontSize = 13,
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
            };

            var valueBlock = new TextBlock
            {
                Text = value ?? "—",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White)
            };

            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(valueBlock, 1);
            row.Children.Add(labelBlock);
            row.Children.Add(valueBlock);
            parent.Children.Add(row);
        }

        private void ClosePlayer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.Stop();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ClosePlayer_Click");
            }
        }


        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ShuffleButton != null)
                {
                    var isShuffled = ShuffleButton.IsChecked ?? false;
                    _musicPlayerService?.SetShuffle(isShuffled);
                    Logger?.LogInformation($"Shuffle set to: {isShuffled}");
                    // Update opacity immediately
                    ShuffleButton.Opacity = isShuffled ? 1.0 : 0.6;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ShuffleButton_Click");
            }
        }

        private void RepeatButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.CycleRepeatMode();
                UpdateRepeatButton();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in RepeatButton_Click");
            }
        }

        private void UpdateShuffleButton()
        {
            if (ShuffleButton != null && _musicPlayerService != null)
            {
                ShuffleButton.IsChecked = _musicPlayerService.IsShuffleEnabled;
                // Update opacity based on state
                ShuffleButton.Opacity = _musicPlayerService.IsShuffleEnabled ? 1.0 : 0.6;
            }
        }

        private void UpdateRepeatButton()
        {
            if (RepeatIcon != null && RepeatButton != null && _musicPlayerService != null)
            {
                switch (_musicPlayerService.RepeatMode)
                {
                    case RepeatMode.None:
                        RepeatIcon.Glyph = "\uE8EE"; // Repeat all icon (subdued)
                        RepeatButton.Opacity = 0.6;
                        break;
                    case RepeatMode.All:
                        RepeatIcon.Glyph = "\uE8EE"; // Repeat all icon (active)
                        RepeatButton.Opacity = 1.0;
                        break;
                    case RepeatMode.One:
                        RepeatIcon.Glyph = "\uE8ED"; // Repeat one icon (active)
                        RepeatButton.Opacity = 1.0;
                        break;
                }
            }
        }

        private void UpdateNavigationButtons()
        {
            if (_musicPlayerService != null && _musicPlayerService.Queue != null)
            {
                var hasPrevious = _musicPlayerService.CurrentQueueIndex > 0;
                var hasNext = _musicPlayerService.CurrentQueueIndex < _musicPlayerService.Queue.Count - 1;

                if (PreviousButton != null)
                {
                    PreviousButton.IsEnabled = hasPrevious;
                }

                if (NextButton != null)
                {
                    NextButton.IsEnabled = hasNext;
                }
            }
        }

        /// <summary>
        /// Sets focus to the play/pause button, making the MusicPlayer the active control
        /// </summary>
        public void FocusPlayPauseButton()
        {
            if (PlayPauseButton != null && Visibility == Visibility.Visible)
            {
                PlayPauseButton.Focus(FocusState.Programmatic);
                Logger?.LogInformation("MusicPlayer: Focus set to PlayPauseButton via trigger hold");
            }
        }


        private void QueueToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isQueueOpen = !_isQueueOpen;
                if (_isQueueOpen)
                {
                    RebuildQueuePanel();
                    QueuePanel.Visibility = Visibility.Visible;
                    QueueToggleButton.Opacity = 1.0;
                    // Adjust the control height to show both panel and bar
                    Height = double.NaN; // Auto
                }
                else
                {
                    CloseQueuePanel();
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in QueueToggleButton_Click");
            }
        }

        private void CloseQueueButton_Click(object sender, RoutedEventArgs e)
        {
            CloseQueuePanel();
        }

        private void CloseQueuePanel()
        {
            _isQueueOpen = false;
            QueuePanel.Visibility = Visibility.Collapsed;
            QueueToggleButton.Opacity = 0.6;
            Height = 84;
        }

        private void HistoryToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isHistoryExpanded = !_isHistoryExpanded;
                if (HistoryItemsPanel != null)
                {
                    HistoryItemsPanel.Visibility = _isHistoryExpanded ? Visibility.Visible : Visibility.Collapsed;
                }
                if (HistoryChevron != null)
                {
                    HistoryChevron.Glyph = _isHistoryExpanded ? "\uE70D" : "\uE76C"; // Down : Right
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in HistoryToggleButton_Click");
            }
        }

        private void RebuildQueuePanel()
        {
            if (_musicPlayerService == null) return;

            var queue = _musicPlayerService.Queue;
            var currentIndex = _musicPlayerService.CurrentQueueIndex;
            var history = _musicPlayerService.PlayedHistory;

            // Previously Played section
            if (history != null && history.Count > 0)
            {
                HistorySection.Visibility = Visibility.Visible;
                HistoryHeaderText.Text = $"Previously Played ({history.Count})";
                HistoryItemsPanel.Children.Clear();

                for (int i = history.Count - 1; i >= 0; i--)
                {
                    var item = history[i];
                    HistoryItemsPanel.Children.Add(CreateQueueItemRow(
                        item, -1, isHistory: true, isCurrent: false));
                }
            }
            else
            {
                HistorySection.Visibility = Visibility.Collapsed;
            }

            // Upcoming items
            UpcomingItemsPanel.Children.Clear();

            if (queue == null || queue.Count == 0) return;

            // Now Playing label is always visible when queue panel is open
            // Show the current track in the upcoming list with highlight
            if (currentIndex >= 0 && currentIndex < queue.Count)
            {
                UpcomingItemsPanel.Children.Add(CreateQueueItemRow(
                    queue[currentIndex], currentIndex, isHistory: false, isCurrent: true));
            }

            // Up next items
            var upcomingItems = _musicPlayerService.GetUpcomingQueue();
            UpNextLabel.Visibility = upcomingItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            for (int i = 0; i < upcomingItems.Count; i++)
            {
                UpcomingItemsPanel.Children.Add(CreateQueueItemRow(
                    upcomingItems[i].Item, upcomingItems[i].QueueIndex, isHistory: false, isCurrent: false));
            }
        }

        private Grid CreateQueueItemRow(BaseItemDto item, int queueIndex, bool isHistory, bool isCurrent)
        {
            var row = new Grid
            {
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(6),
                Background = isCurrent
                    ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF))
                    : null,
            };

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (!isHistory && !isCurrent)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            // Track info
            var info = new StackPanel { VerticalAlignment = Windows.UI.Xaml.VerticalAlignment.Center };

            var nameBlock = new TextBlock
            {
                Text = item?.Name ?? "Unknown Track",
                FontSize = 13,
                FontWeight = isCurrent
                    ? Windows.UI.Text.FontWeights.SemiBold
                    : Windows.UI.Text.FontWeights.Normal,
                Foreground = isCurrent
                    ? new Windows.UI.Xaml.Media.SolidColorBrush(
                        (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"])
                    : isHistory
                        ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF))
                        : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var artistBlock = new TextBlock
            {
                Text = item?.AlbumArtist ?? item?.Artists?.FirstOrDefault() ?? "",
                FontSize = 12,
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(isHistory ? (byte)0x60 : (byte)0x99, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            info.Children.Add(nameBlock);
            if (!string.IsNullOrEmpty(artistBlock.Text))
            {
                info.Children.Add(artistBlock);
            }

            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            // Remove button for upcoming (non-current) items
            if (!isHistory && !isCurrent)
            {
                var removeBtn = new Button
                {
                    Width = 32,
                    Height = 32,
                    Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                    BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon
                    {
                        Glyph = "\uE74D",
                        FontSize = 12,
                        Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                            Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    },
                    Tag = queueIndex,
                    IsTabStop = true,
                    Padding = new Thickness(0),
                    VerticalAlignment = Windows.UI.Xaml.VerticalAlignment.Center,
                };
                removeBtn.Click += RemoveQueueItem_Click;
                removeBtn.Tapped += (s, args) => args.Handled = true;
                Grid.SetColumn(removeBtn, 1);
                row.Children.Add(removeBtn);
            }

            // Make tappable for playing (non-history items)
            if (!isHistory && !isCurrent)
            {
                row.Tag = queueIndex;
                row.Tapped += QueueItemRow_Tapped;
            }

            return row;
        }

        private void RemoveQueueItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is int index)
                {
                    _musicPlayerService?.RemoveFromQueue(index);
                    Logger?.LogInformation($"Removed queue item at index {index}");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in RemoveQueueItem_Click");
            }
        }

        private void QueueItemRow_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                if (sender is Grid grid && grid.Tag is int index)
                {
                    _musicPlayerService?.PlayQueueItemAt(index);
                    Logger?.LogInformation($"Playing queue item at index {index}");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in QueueItemRow_Tapped");
            }
        }


        private void SubscribeToMediaPlayer()
        {
            try
            {
                // Unsubscribe from previous MediaPlayer if any
                UnsubscribeFromMediaPlayer();

                // Subscribe to new MediaPlayer
                if (_musicPlayerService?.MediaPlayer != null)
                {
                    _currentMediaPlayer = _musicPlayerService.MediaPlayer;
                    Logger?.LogInformation("Subscribed to MediaPlayer events");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error subscribing to MediaPlayer events");
            }
        }

        private void UnsubscribeFromMediaPlayer()
        {
            try
            {
                if (_currentMediaPlayer != null)
                {
                    _currentMediaPlayer = null;
                    Logger?.LogInformation("Unsubscribed from MediaPlayer events");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error unsubscribing from MediaPlayer events");
            }
        }
    }
}
