using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;

namespace Gelatinarm.Services
{
    /// <summary>
    /// Service for handling volume normalization using LUFS data from Jellyfin
    /// </summary>
    public class VolumeNormalizationService : BaseService, IVolumeNormalizationService
    {
        private readonly IPreferencesService _preferencesService;
        private readonly ConcurrentDictionary<string, LufsData> _lufsCache = new();
        private const double MIN_VOLUME_MULTIPLIER = 0.1; // Minimum volume (10% of reference)
        private const double MAX_VOLUME_MULTIPLIER = 2.0; // Maximum volume (200% of reference)

        public VolumeNormalizationService(
            ILogger<VolumeNormalizationService> logger,
            IPreferencesService preferencesService) : base(logger)
        {
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        }

        /// <summary>
        /// Applies volume normalization to a MediaPlayer based on the item's LUFS data
        /// </summary>
        public async Task ApplyVolumeNormalizationAsync(MediaPlayer player, BaseItemDto item, CancellationToken cancellationToken = default)
        {
            if (player == null || item == null)
            {
                return;
            }

            var context = CreateErrorContext("ApplyVolumeNormalization", ErrorCategory.Media);
            try
            {
                var prefs = await _preferencesService.GetAppPreferencesAsync().ConfigureAwait(false);

                Logger.LogDebug(
                    "Volume normalization prefs: Enabled={Enable}, UseAlbumGain={UseAlbum}, LufsTarget={Target}",
                    prefs.EnableVolumeNormalization, prefs.UseAlbumGain, prefs.LufsTarget);

                if (!prefs.EnableVolumeNormalization)
                {
                    Logger.LogDebug("Volume normalization is disabled");
                    return;
                }

                var normalizationGain = await GetNormalizationGainAsync(item, prefs.UseAlbumGain, prefs.LufsTarget, cancellationToken).ConfigureAwait(false);

                if (!normalizationGain.HasValue)
                {
                    Logger.LogDebug("No normalization gain/LUFS data available for item {ItemId}", item.Id);
                    return;
                }

                var volumeMultiplier = CalculateVolumeMultiplier(normalizationGain.Value);
                const double referenceVolume = 1.0;
                var finalVolume = Math.Clamp(volumeMultiplier * referenceVolume, 0.0, 1.0);

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    player.Volume = finalVolume;
                }).ConfigureAwait(false);

                Logger.LogInformation(
                    "Applied volume normalization: Item={ItemId}, RawGain={RawGain:F3}, Target={Target:F1}, Multiplier={Multiplier:F3}, FinalVolume={FinalVolume:F3}",
                    item.Id, normalizationGain.Value, prefs.LufsTarget, volumeMultiplier, finalVolume);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        /// <summary>
        /// Gets the normalization gain multiplier for an item, preferring album gain if enabled
        /// </summary>
        private async Task<double?> GetNormalizationGainAsync(BaseItemDto item, bool useAlbumGain, double targetLufs, CancellationToken cancellationToken)
        {
            if (item == null)
            {
                return null;
            }

            var cacheKey = $"{item.Id}_{(useAlbumGain ? "album" : "track")}";

            // Check cache first
            if (_lufsCache.TryGetValue(cacheKey, out var cachedData) &&
                (DateTime.UtcNow - cachedData.Timestamp) < TimeSpan.FromHours(1))
            {
                return cachedData.LufsValue;
            }

            double? gainValue = null;

            try
            {
                var itemType = item.GetType();
                Logger.LogDebug("Getting normalization gain for item {ItemId} at type {Type}", item.Id, itemType.FullName);

                if (useAlbumGain)
                {
                    var albumGainProperty = itemType.GetProperty("AlbumNormalizationGain");
                    if (albumGainProperty != null)
                    {
                        var albumGain = GetNumericValue(albumGainProperty.GetValue(item));
                        if (albumGain.HasValue)
                        {
                            gainValue = CalculateLinearGainFromDb(albumGain.Value);
                            Logger.LogDebug("Using album normalization gain (dB={Db:F2}) => multiplier {Gain:F3}", albumGain.Value, gainValue.Value);
                        }
                        else
                        {
                            Logger.LogDebug("AlbumNormalizationGain property exists but has no value for item {ItemId}", item.Id);
                        }
                    }
                    else
                    {
                        Logger.LogDebug("AlbumNormalizationGain property not found for item {ItemId}", item.Id);
                    }
                }

                if (!gainValue.HasValue)
                {
                    var trackGainProperty = itemType.GetProperty("NormalizationGain");
                    if (trackGainProperty != null)
                    {
                        var trackGain = GetNumericValue(trackGainProperty.GetValue(item));
                        if (trackGain.HasValue)
                        {
                            gainValue = CalculateLinearGainFromDb(trackGain.Value);
                            Logger.LogDebug("Using track normalization gain (dB={Db:F2}) => multiplier {Gain:F3}", trackGain.Value, gainValue.Value);
                        }
                    }
                    else
                    {
                        Logger.LogDebug("NormalizationGain property not found for item {ItemId}", item.Id);
                    }
                }

                if (!gainValue.HasValue)
                {
                    var lufsProperty = itemType.GetProperty("Lufs");
                    if (lufsProperty != null)
                    {
                        var lufsRawValue = GetNumericValue(lufsProperty.GetValue(item));
                        if (lufsRawValue.HasValue)
                        {
                            gainValue = CalculateGainFromLufs(lufsRawValue.Value, targetLufs);
                            Logger.LogDebug("Calculated gain from LUFS {Lufs:F2} to target {Target:F1}: {Gain:F3}", lufsRawValue.Value, targetLufs, gainValue.Value);
                        }
                    }
                    else
                    {
                        Logger.LogDebug("Lufs property not found for item {ItemId}", item.Id);
                    }
                }

                // Check MediaStreams for normalization data if not found on the item directly
                if (!gainValue.HasValue && item.MediaStreams != null)
                {
                    Logger.LogDebug("Checking MediaStreams for normalization data, found {Count} streams", item.MediaStreams.Count);
                    foreach (var stream in item.MediaStreams.Where(s => s.Type == Jellyfin.Sdk.Generated.Models.MediaStream_Type.Audio))
                    {
                        Logger.LogDebug("Checking audio stream {Index} for normalization properties", stream.Index);

                        // Check for normalization properties on the stream
                        var streamType = stream.GetType();
                        var streamNormalizationGain = GetNumericValue(streamType.GetProperty("NormalizationGain")?.GetValue(stream));
                        var streamAlbumGain = GetNumericValue(streamType.GetProperty("AlbumNormalizationGain")?.GetValue(stream));
                        var streamLufs = GetNumericValue(streamType.GetProperty("Lufs")?.GetValue(stream));

                        if (useAlbumGain && streamAlbumGain.HasValue)
                        {
                            gainValue = CalculateLinearGainFromDb(streamAlbumGain.Value);
                            Logger.LogDebug("Found album normalization gain in MediaStream {Index}: {Db:F2} dB => multiplier {Gain:F3}", stream.Index, streamAlbumGain.Value, gainValue.Value);
                            break;
                        }
                        else if (!useAlbumGain && streamNormalizationGain.HasValue)
                        {
                            gainValue = CalculateLinearGainFromDb(streamNormalizationGain.Value);
                            Logger.LogDebug("Found track normalization gain in MediaStream {Index}: {Db:F2} dB => multiplier {Gain:F3}", stream.Index, streamNormalizationGain.Value, gainValue.Value);
                            break;
                        }
                        else if (streamLufs.HasValue)
                        {
                            gainValue = CalculateGainFromLufs(streamLufs.Value, targetLufs);
                            Logger.LogDebug("Found LUFS in MediaStream {Index}: {Lufs:F2} => multiplier {Gain:F3}", stream.Index, streamLufs.Value, gainValue.Value);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error accessing normalization properties from BaseItemDto or MediaStreams");
            }

            if (gainValue.HasValue)
            {
                _lufsCache[cacheKey] = new LufsData
                {
                    LufsValue = gainValue.Value,
                    Timestamp = DateTime.UtcNow
                };
            }

            return gainValue;
        }

        /// <summary>
        /// Calculates volume gain from LUFS value to target LUFS level
        /// </summary>
        private double CalculateGainFromLufs(double lufs, double targetLufs)
        {
            // Convert LUFS to linear gain: gain = 10^(LUFS_diff/20)
            // Positive LUFS means quieter than reference, so we boost volume
            // Negative LUFS means louder than reference, so we reduce volume
            var lufsDiff = targetLufs - lufs;
            return Math.Pow(10, lufsDiff / 20.0);
        }

        private static double? GetNumericValue(object value)
        {
            if (value is null)
            {
                return null;
            }

            if (value is double numericDouble)
            {
                return numericDouble;
            }
            if (value is float numericFloat)
            {
                return numericFloat;
            }
            if (value is int numericInt)
            {
                return numericInt;
            }
            if (value is long numericLong)
            {
                return numericLong;
            }
            if (value is decimal numericDecimal)
            {
                return (double)numericDecimal;
            }
            if (value is double && value != null)
            {
                return (double)value;
            }
            if (value is float && value != null)
            {
                return (float)value;
            }
            if (value is int && value != null)
            {
                return (int)value;
            }
            if (value is long && value != null)
            {
                return (long)value;
            }
            if (value is decimal && value != null)
            {
                return (double)(decimal)value;
            }

            return null;
        }

        private double CalculateLinearGainFromDb(double dbGain)
        {
            // Convert a dB gain value to a linear multiplier
            return Math.Pow(10, dbGain / 20.0);
        }

        /// <summary>
        /// Calculates the final volume multiplier from gain value
        /// </summary>
        private double CalculateVolumeMultiplier(double gain)
        {
            // The gain value is a linear multiplier and is clamped to avoid extreme volume changes.
            return Math.Clamp(gain, MIN_VOLUME_MULTIPLIER, MAX_VOLUME_MULTIPLIER);
        }

        /// <summary>
        /// Clears the LUFS cache
        /// </summary>
        public void ClearCache()
        {
            _lufsCache.Clear();
            Logger.LogInformation("LUFS cache cleared");
        }

        /// <summary>
        /// Gets the current volume multiplier for an item without applying it
        /// </summary>
        public async Task<double?> GetVolumeMultiplierAsync(BaseItemDto item, CancellationToken cancellationToken = default)
        {
            var prefs = await _preferencesService.GetAppPreferencesAsync().ConfigureAwait(false);

            if (!prefs.EnableVolumeNormalization)
            {
                return null;
            }

            var gainValue = await GetNormalizationGainAsync(item, prefs.UseAlbumGain, prefs.LufsTarget, cancellationToken).ConfigureAwait(false);
            return gainValue.HasValue ? CalculateVolumeMultiplier(gainValue.Value) : (double?)null;
        }

        private class LufsData
        {
            public double LufsValue { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}