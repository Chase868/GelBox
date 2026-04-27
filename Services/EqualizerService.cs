using System;
using System.Threading.Tasks;
using GelBox.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Render;

namespace GelBox.Services
{
    /// <summary>
    /// Applies a 6-band parametric equalizer to audio and/or video MediaPlayer instances
    /// using the Windows AudioGraph DSP pipeline.
    ///
    /// For MUSIC: a separate AudioGraph is created from the track URI (a parallel pipeline
    /// distinct from the MediaPlayer).  The MediaPlayer is muted while the graph is active so
    /// only the EQ-processed audio is heard.  Play/pause state is kept in sync via the
    /// MediaPlayer's PlaybackSession.PlaybackStateChanged event.
    ///
    /// For VIDEO: the existing MediaSource-based approach is used (best-effort).
    ///
    /// Windows.Media.Audio.EqualizerEffectDefinition is fixed at 4 bands per instance, so two
    /// chained effects cover all 6 user bands:
    ///   Effect 1 (eq1): bands 0-3  →  60 Hz, 180 Hz, 500 Hz, 1400 Hz
    ///   Effect 2 (eq2): bands 0-1  →  4000 Hz, 11000 Hz  (bands 2-3 are neutral passthrough)
    /// </summary>
    public class EqualizerService : BaseService, IEqualizerService
    {
        private readonly IPreferencesService _preferencesService;

        // Frequencies for each of the 6 user-visible bands (Hz)
        public static readonly double[] BandFrequencies = { 60, 180, 500, 1400, 4000, 11000 };

        // ── Audio player reference ───────────────────────────────────────────
        private MediaPlayer _attachedAudioPlayer;
        private bool _audioPlayerEventSubscribed;
        private bool _audioPlayerVolumeEventSubscribed;
        private bool _audioPlayerMutedByEq;
        private Uri _lastAudioUri;

        // ── Audio player graph ──────────────────────────────────────────────
        private AudioGraph _audioGraph;
        private MediaSourceAudioInputNode _audioInputNode;
        private EqualizerEffectDefinition _audioEq1; // bands 0-3
        private EqualizerEffectDefinition _audioEq2; // bands 4-5
        private bool _audioGraphReady; // graph created, waiting for or already started

        // ── Video player graph ──────────────────────────────────────────────
        private AudioGraph _videoGraph;
        private MediaSourceAudioInputNode _videoInputNode;
        private EqualizerEffectDefinition _videoEq1;
        private EqualizerEffectDefinition _videoEq2;
        private MediaPlayer _attachedVideoPlayer;

        // ── EQ state ────────────────────────────────────────────────────────
        private bool _enabled;
        private bool _eqForVideoEnabled;
        private bool _prefsLoaded;
        private bool _graphAttachDisabledForSession;
        private bool _audioLayoutConfigured;
        private int _audioEqGainFailureCount;
        private readonly double[] _bandGains = new double[6]; // all 0.0 by default

        public bool IsEnabled => _enabled;
        public bool IsEqForVideoEnabled => _eqForVideoEnabled;

        public EqualizerService(ILogger<EqualizerService> logger, IPreferencesService preferencesService)
            : base(logger)
        {
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        }

        // ── IEqualizerService ───────────────────────────────────────────────

        public async Task LoadPreferencesAsync()
        {
            try
            {
                var prefs = await _preferencesService.GetAppPreferencesAsync().ConfigureAwait(false);
                _enabled = prefs.EqualizerEnabled;
                _eqForVideoEnabled = prefs.ApplyEqToVideo;
                for (int i = 0; i < 6; i++)
                    _bandGains[i] = prefs.GetEqBandGain(i);
                _prefsLoaded = true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to load EQ preferences, using defaults");
            }
        }

        /// <summary>
        /// Registers the music MediaPlayer so EQ can stay in sync with play/pause.
        /// The actual AudioGraph is NOT created here; it is created in SetAudioSourceAsync
        /// once the track URI is known.
        /// </summary>
        public async Task AttachToAudioPlayerAsync(MediaPlayer player)
        {
            if (player == null) return;

            if (!_prefsLoaded)
                await LoadPreferencesAsync().ConfigureAwait(false);

            if (_attachedAudioPlayer != player)
            {
                UnsubscribeAudioPlayerEvents();
                _attachedAudioPlayer = player;
                SubscribeAudioPlayerEvents();
                SubscribeAudioPlayerVolumeEvents();
            }
            Logger.LogInformation("EQ audio player reference registered");
        }

        public async Task DetachAudioAsync()
        {
            try
            {
                _audioGraph?.Stop();
                _audioGraph?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error disposing audio EQ graph");
            }
            finally
            {
                _audioGraph = null;
                _audioInputNode = null;
                _audioEq1 = null;
                _audioEq2 = null;
                _audioGraphReady = false;

                if (_audioPlayerMutedByEq && _attachedAudioPlayer != null)
                {
                    try { _attachedAudioPlayer.IsMuted = false; } catch { }
                    _audioPlayerMutedByEq = false;
                }

                UnsubscribeAudioPlayerEvents();
                UnsubscribeAudioPlayerVolumeEvents();
                _attachedAudioPlayer = null;
                await Task.CompletedTask;
            }
        }

        /// <summary>
        /// Informs the EQ service of the URI for the track that is about to play.
        /// When EQ is enabled this builds a fresh AudioGraph from that URI and mutes the
        /// MediaPlayer while the graph is active.  When EQ is disabled the URI is stored so
        /// the graph can be created if EQ is enabled later.
        /// </summary>
        public async Task SetAudioSourceAsync(Uri streamUri)
        {
            if (streamUri == null) return;
            _lastAudioUri = streamUri;

            if (!_enabled) return; // graph will be created when EQ is toggled on
            await SetAudioSourceInternalAsync(streamUri).ConfigureAwait(false);
        }

        public async Task AttachToVideoPlayerAsync(MediaPlayer player)
        {
            if (player == null) return;
            if (_graphAttachDisabledForSession) return;

            if (!_prefsLoaded)
                await LoadPreferencesAsync().ConfigureAwait(false);

            // If already attached to this exact player instance, just sync bypass state
            if (player == _attachedVideoPlayer)
            {
                if (_videoInputNode != null && _videoEq1 != null && _videoEq2 != null)
                    BypassEffects(_videoInputNode, _videoEq1, _videoEq2, !_enabled || !_eqForVideoEnabled);
                return;
            }

            try
            {
                await DetachVideoAsync().ConfigureAwait(false);
                var (graph, inputNode, eq1, eq2) = await CreateGraphFromPlayerAsync(player).ConfigureAwait(false);
                if (graph == null) return;

                _videoGraph = graph;
                _videoInputNode = inputNode;
                _videoEq1 = eq1;
                _videoEq2 = eq2;
                _attachedVideoPlayer = player;

                ApplyGainsToEffects(eq1, eq2, _videoGraph?.EncodingProperties?.SampleRate, configureLayout: true);
                BypassEffects(inputNode, eq1, eq2, !_enabled || !_eqForVideoEnabled);
                graph.Start();
                Logger.LogInformation("EQ attached to video player");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to attach EQ to video player");
            }
        }

        public async Task DetachVideoAsync()
        {
            try
            {
                _videoGraph?.Stop();
                _videoGraph?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error disposing video EQ graph");
            }
            finally
            {
                _videoGraph = null;
                _videoInputNode = null;
                _videoEq1 = null;
                _videoEq2 = null;
                _attachedVideoPlayer = null;
                await Task.CompletedTask;
            }
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (enabled)
            {
                // User turned EQ on mid-playback. Rebuild/reattach graph for the current track
                // immediately so EQ becomes audible without waiting for the next song.
                if (_lastAudioUri != null)
                    _ = Task.Run(() => SetAudioSourceInternalAsync(_lastAudioUri));
            }
            else
            {
                // User turned EQ off — stop graph and restore MediaPlayer audio.
                // Dispose the graph so a subsequent enable forces a fresh attach to current track.
                try
                {
                    _audioGraph?.Stop();
                    _audioGraph?.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Error disposing audio EQ graph while disabling EQ");
                }
                _audioGraph = null;
                _audioInputNode = null;
                _audioEq1 = null;
                _audioEq2 = null;
                _audioGraphReady = false;
                _audioLayoutConfigured = false;
                if (_audioPlayerMutedByEq && _attachedAudioPlayer != null)
                {
                    try { _attachedAudioPlayer.IsMuted = false; } catch { }
                    _audioPlayerMutedByEq = false;
                }
            }
            UpdateEnabledState();
        }

        public void SetEqForVideoEnabled(bool enabled)
        {
            _eqForVideoEnabled = enabled;
            if (_videoInputNode != null && _videoEq1 != null && _videoEq2 != null)
                BypassEffects(_videoInputNode, _videoEq1, _videoEq2, !_enabled || !_eqForVideoEnabled);
        }

        public void SetBandGain(int bandIndex, double gainDb)
        {
            if (bandIndex < 0 || bandIndex >= 6) return;
            _bandGains[bandIndex] = Math.Clamp(gainDb, -12.0, 12.0);
            if (_audioGraphReady)
                ApplyGainsToEffects(_audioEq1, _audioEq2, _audioGraph?.EncodingProperties?.SampleRate, configureLayout: false);
            ApplyGainsToEffects(_videoEq1, _videoEq2, _videoGraph?.EncodingProperties?.SampleRate, configureLayout: false);
        }

        public double GetBandGain(int bandIndex)
        {
            if (bandIndex < 0 || bandIndex >= 6) return 0.0;
            return _bandGains[bandIndex];
        }

        // ── Audio source / sync helpers ─────────────────────────────────────

        private async Task SetAudioSourceInternalAsync(Uri streamUri)
        {
            if (_graphAttachDisabledForSession) return;
            if (!_prefsLoaded)
                await LoadPreferencesAsync().ConfigureAwait(false);

            // Dispose old graph without touching player mute state
            try
            {
                _audioGraph?.Stop();
                _audioGraph?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error disposing old audio EQ graph before source swap");
            }
            _audioGraph = null;
            _audioInputNode = null;
            _audioEq1 = null;
            _audioEq2 = null;
            _audioGraphReady = false;
            _audioLayoutConfigured = false;

            try
            {
                var mediaSource = MediaSource.CreateFromUri(streamUri);
                var (graph, inputNode, eq1, eq2) = await BuildGraphFromMediaSourceAsync(mediaSource).ConfigureAwait(false);
                if (graph == null) return;

                _audioGraph = graph;
                _audioInputNode = inputNode;
                _audioEq1 = eq1;
                _audioEq2 = eq2;

                // Mirror normalization volume to graph output gain so loudness is consistent
                // whether EQ is on or off.  Do this BEFORE muting the player so the event
                // subscription fires correctly if volume changes later.
                if (_attachedAudioPlayer != null)
                {
                    try { _audioInputNode.OutgoingGain = Math.Clamp(_attachedAudioPlayer.Volume, 0.0, 1.0); }
                    catch (Exception ex) { Logger.LogWarning(ex, "Failed to set initial EQ graph outgoing gain"); }
                }

                // Enable EQ effects on the node (they are added enabled by default but be explicit).
                BypassEffects(inputNode, eq1, eq2, !_enabled);

                // Mute the MediaPlayer so only the EQ-processed AudioGraph output is heard.
                if (_attachedAudioPlayer != null && !_attachedAudioPlayer.IsMuted)
                {
                    _attachedAudioPlayer.IsMuted = true;
                    _audioPlayerMutedByEq = true;
                }

                _audioGraphReady = true;

                // Start graph (and apply FXEQ parameters after start — FXEQ only reliably
                // accepts writes while the processing pipeline is active).
                // OnAudioPlaybackStateChanged handles future play/pause transitions.
                if (_attachedAudioPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing)
                    StartGraphSynced();

                Logger.LogInformation("EQ AudioGraph created for new audio source");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to create EQ AudioGraph for audio source");
            }
        }

        private void SubscribeAudioPlayerEvents()
        {
            if (_attachedAudioPlayer == null || _audioPlayerEventSubscribed) return;
            _attachedAudioPlayer.PlaybackSession.PlaybackStateChanged += OnAudioPlaybackStateChanged;
            _audioPlayerEventSubscribed = true;
        }

        private void SubscribeAudioPlayerVolumeEvents()
        {
            if (_attachedAudioPlayer == null || _audioPlayerVolumeEventSubscribed) return;
            _attachedAudioPlayer.VolumeChanged += OnAudioPlayerVolumeChanged;
            _audioPlayerVolumeEventSubscribed = true;
        }

        private void UnsubscribeAudioPlayerEvents()
        {
            if (_attachedAudioPlayer == null || !_audioPlayerEventSubscribed) return;
            try { _attachedAudioPlayer.PlaybackSession.PlaybackStateChanged -= OnAudioPlaybackStateChanged; }
            catch { }
            _audioPlayerEventSubscribed = false;
        }

        private void UnsubscribeAudioPlayerVolumeEvents()
        {
            if (_attachedAudioPlayer == null || !_audioPlayerVolumeEventSubscribed) return;
            try { _attachedAudioPlayer.VolumeChanged -= OnAudioPlayerVolumeChanged; }
            catch { }
            _audioPlayerVolumeEventSubscribed = false;
        }

        private void OnAudioPlayerVolumeChanged(MediaPlayer sender, object args)
        {
            if (_audioInputNode == null) return;
            try
            {
                _audioInputNode.OutgoingGain = Math.Clamp(sender.Volume, 0.0, 1.0);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to sync EQ graph outgoing gain from MediaPlayer volume");
            }
        }

        private void OnAudioPlaybackStateChanged(MediaPlaybackSession session, object args)
        {
            if (!_audioGraphReady || _audioGraph == null || _audioInputNode == null) return;
            try
            {
                switch (session.PlaybackState)
                {
                    case MediaPlaybackState.Playing:
                        StartGraphSynced();
                        break;
                    case MediaPlaybackState.Paused:
                    case MediaPlaybackState.None:
                        _audioGraph.Stop();
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error syncing audio EQ graph state with player");
            }
        }

        private void StartGraphSynced()
        {
            if (_audioInputNode == null || _audioGraph == null) return;
            try
            {
                // Seek to match the MediaPlayer's current position so they stay in sync.
                var position = _attachedAudioPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
                _audioInputNode.Seek(position);

                // Start graph FIRST — FXEQ (EqualizerEffectDefinition) is an XAPO processor
                // whose parameter block is only reliably accepted while the pipeline is running.
                // Any writes before Start() are silently discarded by the driver.
                _audioGraph.Start();

                // Now that the pipeline is active, configure band layout (once) then write gains.
                ApplyGainsToEffects(
                    _audioEq1, _audioEq2,
                    _audioGraph?.EncodingProperties?.SampleRate,
                    configureLayout: !_audioLayoutConfigured);
                _audioLayoutConfigured = true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error starting EQ audio graph in sync with player");
            }
        }

        // ── Graph creation ──────────────────────────────────────────────────

        /// <summary>
        /// Extracts a MediaSource from a <paramref name="player"/> and builds a graph.
        /// Used for the video path where the source is already loaded on the player.
        /// </summary>
        private async Task<(AudioGraph graph, MediaSourceAudioInputNode inputNode,
            EqualizerEffectDefinition eq1, EqualizerEffectDefinition eq2)> CreateGraphFromPlayerAsync(MediaPlayer player)
        {
            if (player?.Source == null)
            {
                Logger.LogWarning("Cannot create EQ graph: MediaPlayer source is null");
                return (null, null, null, null);
            }

            MediaSource mediaSource = null;
            if (player.Source is MediaSource directMediaSource)
                mediaSource = directMediaSource;
            else if (player.Source is MediaPlaybackItem playbackItem)
                mediaSource = playbackItem.Source as MediaSource;

            if (mediaSource == null)
            {
                Logger.LogWarning("Cannot create EQ graph: MediaPlayer source is not a MediaSource");
                return (null, null, null, null);
            }

            return await BuildGraphFromMediaSourceAsync(mediaSource).ConfigureAwait(false);
        }

        private async Task<(AudioGraph graph, MediaSourceAudioInputNode inputNode,
            EqualizerEffectDefinition eq1, EqualizerEffectDefinition eq2)> BuildGraphFromMediaSourceAsync(MediaSource mediaSource)
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media);
            var graphResult = await AudioGraph.CreateAsync(settings).AsTask().ConfigureAwait(false);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
            {
                Logger.LogWarning("AudioGraph creation failed: {Status}", graphResult.Status);
                return (null, null, null, null);
            }

            var graph = graphResult.Graph;

            var outputResult = await graph.CreateDeviceOutputNodeAsync().AsTask().ConfigureAwait(false);
            if (outputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                Logger.LogWarning("Device output node creation failed: {Status}", outputResult.Status);
                graph.Dispose();
                return (null, null, null, null);
            }

            var inputResult = await graph.CreateMediaSourceAudioInputNodeAsync(mediaSource).AsTask().ConfigureAwait(false);
            if (inputResult.Status != MediaSourceAudioInputNodeCreationStatus.Success)
            {
                Logger.LogWarning("MediaSource input node creation failed: {Status}", inputResult.Status);
                if (inputResult.Status == MediaSourceAudioInputNodeCreationStatus.UnknownFailure)
                {
                    _graphAttachDisabledForSession = true;
                    Logger.LogWarning("Disabling EQ graph attachment for this app session after UnknownFailure.");
                }
                graph.Dispose();
                return (null, null, null, null);
            }

            var inputNode = inputResult.Node;
            var outputNode = outputResult.DeviceOutputNode;

            // Two chained 4-band EQ effects to represent 6 user bands
            var eq1 = new EqualizerEffectDefinition(graph);
            var eq2 = new EqualizerEffectDefinition(graph);
            inputNode.EffectDefinitions.Add(eq1);
            inputNode.EffectDefinitions.Add(eq2);
            inputNode.AddOutgoingConnection(outputNode);

            return (graph, inputNode, eq1, eq2);
        }

        // ── Effect helpers ──────────────────────────────────────────────────

        private void ApplyGainsToEffects(
            EqualizerEffectDefinition eq1,
            EqualizerEffectDefinition eq2,
            uint? sampleRate,
            bool configureLayout)
        {
            if (eq1 == null || eq2 == null) return;
            if (eq1.Bands.Count < 4 || eq2.Bands.Count < 4) return;

            double nyquistLimit = GetNyquistSafeUpperFrequency(sampleRate);

            if (configureLayout)
            {
                // eq1: user bands 0-3 map directly to effect bands 0-3.
                // FXEQ requires ALL 4 bands to be in strict ascending frequency order.
                double[] eq1Freqs = BuildAscendingFrequencies(
                    new[] { BandFrequencies[0], BandFrequencies[1], BandFrequencies[2], BandFrequencies[3] },
                    20.0, nyquistLimit);

                TrySetBandLayout(eq1.Bands[0], eq1Freqs[0], 1.5, "eq1-b0");
                TrySetBandLayout(eq1.Bands[1], eq1Freqs[1], 1.5, "eq1-b1");
                TrySetBandLayout(eq1.Bands[2], eq1Freqs[2], 1.5, "eq1-b2");
                TrySetBandLayout(eq1.Bands[3], eq1Freqs[3], 2.0, "eq1-b3");

                // eq2: user band 4 → effect band 0, user band 5 → effect band 3.
                // Bands 1 and 2 are neutral fill bands inserted so ALL 4 bands remain in
                // strict ascending frequency order.  Leaving bands 2-3 at their Windows FXEQ
                // defaults (~2200 Hz and ~5000 Hz) after we set bands 0-1 to 3000/11000 Hz
                // produces the ordering 3000→11000→2200→5000 which violates the ascending
                // constraint and causes FXEQ to reject every subsequent gain write.
                double b4    = Math.Clamp(BandFrequencies[4], 20.0, nyquistLimit); // 4000 Hz
                double b5    = Math.Clamp(BandFrequencies[5], 20.0, nyquistLimit); // 11000 Hz
                double fill1 = Math.Clamp((b4 * 2.0 + b5) / 3.0, b4 + 100.0, b5 - 200.0);
                double fill2 = Math.Clamp((b4 + b5 * 2.0) / 3.0, fill1 + 100.0, b5 - 100.0);

                TrySetBandLayout(eq2.Bands[0], b4,    1.5, "eq2-b0");
                TrySetBandLayout(eq2.Bands[1], fill1, 1.0, "eq2-b1-fill");
                TrySetBandLayout(eq2.Bands[2], fill2, 1.0, "eq2-b2-fill");
                TrySetBandLayout(eq2.Bands[3], b5,    2.0, "eq2-b3");
            }

            // eq1: user bands 0-3
            TrySetBandGain(eq1.Bands[0], _bandGains[0], "eq1-b0");
            TrySetBandGain(eq1.Bands[1], _bandGains[1], "eq1-b1");
            TrySetBandGain(eq1.Bands[2], _bandGains[2], "eq1-b2");
            TrySetBandGain(eq1.Bands[3], _bandGains[3], "eq1-b3");

            // eq2: user band 4 at index 0, neutral fills at 1 & 2 (0 dB = gain 1.0), user band 5 at index 3
            TrySetBandGain(eq2.Bands[0], _bandGains[4], "eq2-b0");
            TrySetBandGain(eq2.Bands[1], 0.0,           "eq2-b1-fill");
            TrySetBandGain(eq2.Bands[2], 0.0,           "eq2-b2-fill");
            TrySetBandGain(eq2.Bands[3], _bandGains[5], "eq2-b3");
        }

        private static double GetNyquistSafeUpperFrequency(uint? sampleRate)
        {
            // Keep a small guard below Nyquist to avoid endpoint validation failures.
            var rate = sampleRate.GetValueOrDefault(48000u);
            var nyquist = (rate / 2.0) - 50.0;
            return Math.Max(200.0, nyquist);
        }

        private static double[] BuildAscendingFrequencies(double[] preferred, double minFrequency, double maxFrequency)
        {
            var result = new double[preferred.Length];
            double previous = minFrequency - 1.0;

            for (int i = 0; i < preferred.Length; i++)
            {
                double clamped = Math.Clamp(preferred[i], minFrequency, maxFrequency);
                if (clamped <= previous)
                {
                    clamped = previous + 1.0;
                }
                result[i] = Math.Min(clamped, maxFrequency);
                previous = result[i];
            }

            // If the range is too tight to keep strict ordering, spread evenly.
            for (int i = 1; i < result.Length; i++)
            {
                if (result[i] <= result[i - 1])
                {
                    double span = Math.Max(4.0, maxFrequency - minFrequency);
                    double step = span / (result.Length + 1);
                    for (int j = 0; j < result.Length; j++)
                    {
                        result[j] = minFrequency + ((j + 1) * step);
                    }
                    break;
                }
            }

            return result;
        }

        private void TrySetBandLayout(EqualizerBand band, double frequencyHz, double bandwidth, string bandName)
        {
            try
            {
                band.FrequencyCenter = frequencyHz;
                band.Bandwidth = bandwidth;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "EQ band layout failed for {Band}: F={Freq}Hz BW={BW}", bandName, frequencyHz, bandwidth);
            }
        }

        private void TrySetBandGain(EqualizerBand band, double gainDb, string bandName)
        {
            try
            {
                // EqualizerBand.Gain uses linear gain (see UWP AudioCreation sample / FXEQ range).
                // Clamp to sample's demonstrated safe range: 0.126 to 7.94.
                double clampedDb = Math.Clamp(gainDb, -12.0, 12.0);
                double linearGain = Math.Pow(10.0, clampedDb / 20.0);
                band.Gain = Math.Clamp(linearGain, 0.126, 7.94);
            }
            catch (Exception ex)
            {
                // Keep graph alive even if a specific band's gain cannot be updated.
                Logger.LogWarning(ex, "EQ band gain set failed for {Band} with G={Gain}dB", bandName, gainDb);
                _audioEqGainFailureCount++;
            }
        }

        private static void BypassEffects(
            MediaSourceAudioInputNode node,
            EqualizerEffectDefinition eq1,
            EqualizerEffectDefinition eq2,
            bool bypass)
        {
            if (node == null || eq1 == null || eq2 == null) return;
            if (bypass)
            {
                node.DisableEffectsByDefinition(eq1);
                node.DisableEffectsByDefinition(eq2);
            }
            else
            {
                node.EnableEffectsByDefinition(eq1);
                node.EnableEffectsByDefinition(eq2);
            }
        }

        private void UpdateEnabledState()
        {
            if (_audioInputNode != null && _audioEq1 != null && _audioEq2 != null)
                BypassEffects(_audioInputNode, _audioEq1, _audioEq2, !_enabled);

            if (_videoInputNode != null && _videoEq1 != null && _videoEq2 != null)
                BypassEffects(_videoInputNode, _videoEq1, _videoEq2, !_enabled || !_eqForVideoEnabled);
        }
    }
}
