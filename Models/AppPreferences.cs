using System;
using GelBox.Constants;

namespace GelBox.Models
{
    /// <summary>
    ///     Consolidated app preferences model combining user and playback preferences
    /// </summary>
    public class AppPreferences
    {
        // === UI Preferences ===
        public int ControlsHideDelay { get; set; } = 3; // seconds

        public string VideoStretchMode { get; set; } =
            "Uniform"; // Uniform (black bars) or UniformToFill (no black bars)

        // === Playback Behavior ===
        public bool AutoPlayNextEpisode { get; set; } = true;
        public bool AutoSkipIntroEnabled { get; set; } = false;
        public bool AutoSkipOutroEnabled { get; set; } = false;
        public bool PauseOnFocusLoss { get; set; } = false; // Whether to pause when Xbox guide is opened
        public bool RestorePlaybackOnLaunch { get; set; } = true; // Whether to resume music queue after app restart
        public bool AutoPlayAfterRestoreOnLaunch { get; set; } = false; // Whether restored queue starts playing automatically

        // === Skip Settings ===
        // Skip values are currently hardcoded: forward=30s, backward=10s

        // === Audio & Subtitle Preferences ===
        public int DefaultSubtitleStreamIndex { get; set; } = -1;

        // === Volume Normalization ===
        public bool EnableVolumeNormalization { get; set; } = true; // Enable gain-based volume normalization
        public bool UseAlbumGain { get; set; } = false; // Use album-level normalization instead of track-level
        public double VolumeOffsetDb { get; set; } = 0.0; // Additional dB offset applied on top of normalization (-10 to +10)

        // === Equalizer ===
        public bool EqualizerEnabled { get; set; } = false;
        public bool ApplyEqToVideo { get; set; } = false;
        // 6 bands: 60 Hz, 180 Hz, 500 Hz, 1.4 kHz, 4.0 kHz, 11 kHz — flat by default
        public double EqBand0Gain { get; set; } = 0.0;
        public double EqBand1Gain { get; set; } = 0.0;
        public double EqBand2Gain { get; set; } = 0.0;
        public double EqBand3Gain { get; set; } = 0.0;
        public double EqBand4Gain { get; set; } = 0.0;
        public double EqBand5Gain { get; set; } = 0.0;

        public double GetEqBandGain(int index) => index switch
        {
            0 => EqBand0Gain,
            1 => EqBand1Gain,
            2 => EqBand2Gain,
            3 => EqBand3Gain,
            4 => EqBand4Gain,
            5 => EqBand5Gain,
            _ => 0.0
        };

        // === Network & Streaming ===
        public bool EnableDirectPlay { get; set; } = true; // Allow direct play when format is compatible
        public bool AllowAudioStreamCopy { get; set; } = false; // Default to false to avoid audio compatibility issues

        // === UI Settings ===
        public double TextSize { get; set; } = 14.0;

        // === Appearance Settings ===
        public bool EnableGradientBackground { get; set; } = true;

        // === Home Screen Settings ===
        public bool ShowMoviesOnHome { get; set; } = true;
        public bool ShowTVShowsOnHome { get; set; } = true;
        public bool ShowMusicOnHome { get; set; } = true;

        // === Connection Settings ===
        public int ConnectionTimeout { get; set; } = SystemConstants.DEFAULT_TIMEOUT_SECONDS;
        public bool IgnoreCertificateErrors { get; set; } = true;

        // === Metadata ===
        public DateTime LastModified { get; set; } = DateTime.Now;
    }
}
