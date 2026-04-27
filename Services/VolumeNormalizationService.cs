using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GelBox.Constants;
using GelBox.Helpers;
using GelBox.Models;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;

namespace GelBox.Services
{
    /// <summary>
    /// Service for handling volume normalization using NormalizationGain dB data from Jellyfin,
    /// with an optional user-configurable dB offset.
    /// </summary>
    public class VolumeNormalizationService : BaseService, IVolumeNormalizationService
    {
        private readonly IPreferencesService _preferencesService;
        private readonly ConcurrentDictionary<string, GainCacheEntry> _gainCache = new();
        private const double MIN_VOLUME_MULTIPLIER = 0.1;
        private const double MAX_VOLUME_MULTIPLIER = 2.0;

        public VolumeNormalizationService(
            ILogger<VolumeNormalizationService> logger,
            IPreferencesService preferencesService) : base(logger)
        {
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        }

        /// <summary>
        /// Applies volume normalization to a MediaPlayer based on the item's stored dB gain.
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
                    "Volume normalization prefs: Enabled={Enable}, UseAlbumGain={UseAlbum}, VolumeOffsetDb={Offset}",
                    prefs.EnableVolumeNormalization, prefs.UseAlbumGain, prefs.VolumeOffsetDb);

                if (!prefs.EnableVolumeNormalization)
                {
                    Logger.LogDebug("Volume normalization is disabled");
                    return;
                }

                var gainDb = GetStoredGainDb(item, prefs.UseAlbumGain);

                if (!gainDb.HasValue)
                {
                    Logger.LogDebug("No normalization gain data available for item {ItemId}", item.Id);
                    return;
                }

                var totalDb = gainDb.Value + prefs.VolumeOffsetDb;
                var multiplier = CalculateVolumeMultiplier(CalculateLinearGainFromDb(totalDb));
                var finalVolume = Math.Clamp(multiplier, 0.0, 1.0);

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    player.Volume = finalVolume;
                }).ConfigureAwait(false);

                Logger.LogInformation(
                    "Applied volume normalization: Item={ItemId}, StoredGain={StoredGain:+0.00;-0.00} dB, Offset={Offset:+0.0;-0.0} dB, Total={Total:+0.00;-0.00} dB, FinalVolume={FinalVolume:F3}",
                    item.Id, gainDb.Value, prefs.VolumeOffsetDb, totalDb, finalVolume);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        /// <summary>
        /// Reads the stored NormalizationGain (or AlbumNormalizationGain) dB value from the item,
        /// checking both top-level properties and MediaStreams. Returns null if not present.
        /// </summary>
        private double? GetStoredGainDb(BaseItemDto item, bool useAlbumGain)
        {
            if (item == null) return null;

            var cacheKey = $"{item.Id}_{(useAlbumGain ? "album" : "track")}";
            if (_gainCache.TryGetValue(cacheKey, out var cached) &&
                (DateTime.UtcNow - cached.Timestamp) < TimeSpan.FromHours(1))
            {
                return cached.GainDb;
            }

            double? gainDb = null;
            try
            {
                var itemType = item.GetType();

                if (useAlbumGain)
                {
                    gainDb = GetNumericValue(itemType.GetProperty("AlbumNormalizationGain")?.GetValue(item));
                    if (!gainDb.HasValue && item.MediaStreams != null)
                    {
                        foreach (var s in item.MediaStreams.Where(s => s.Type == MediaStream_Type.Audio))
                        {
                            gainDb = GetNumericValue(s.GetType().GetProperty("AlbumNormalizationGain")?.GetValue(s));
                            if (gainDb.HasValue) break;
                        }
                    }
                }

                if (!gainDb.HasValue)
                {
                    gainDb = GetNumericValue(itemType.GetProperty("NormalizationGain")?.GetValue(item));
                    if (!gainDb.HasValue && item.MediaStreams != null)
                    {
                        foreach (var s in item.MediaStreams.Where(s => s.Type == MediaStream_Type.Audio))
                        {
                            gainDb = GetNumericValue(s.GetType().GetProperty("NormalizationGain")?.GetValue(s));
                            if (gainDb.HasValue) break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error reading normalization gain for item {ItemId}", item.Id);
            }

            _gainCache[cacheKey] = new GainCacheEntry { GainDb = gainDb, Timestamp = DateTime.UtcNow };
            return gainDb;
        }

        private static double? GetNumericValue(object value)
        {
            return value switch
            {
                null => null,
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                decimal m => (double)m,
                _ => null
            };
        }

        private static double CalculateLinearGainFromDb(double dbGain)
            => Math.Pow(10, dbGain / 20.0);

        private static double CalculateVolumeMultiplier(double gain)
            => Math.Clamp(gain, MIN_VOLUME_MULTIPLIER, MAX_VOLUME_MULTIPLIER);

        /// <summary>
        /// Clears the gain cache.
        /// </summary>
        public void ClearCache()
        {
            _gainCache.Clear();
            Logger.LogInformation("Normalization gain cache cleared");
        }

        /// <summary>
        /// Gets the current volume multiplier for an item without applying it.
        /// </summary>
        public async Task<double?> GetVolumeMultiplierAsync(BaseItemDto item, CancellationToken cancellationToken = default)
        {
            var prefs = await _preferencesService.GetAppPreferencesAsync().ConfigureAwait(false);
            if (!prefs.EnableVolumeNormalization) return null;

            var gainDb = GetStoredGainDb(item, prefs.UseAlbumGain);
            if (!gainDb.HasValue) return null;

            var totalDb = gainDb.Value + prefs.VolumeOffsetDb;
            return CalculateVolumeMultiplier(CalculateLinearGainFromDb(totalDb));
        }

        /// <summary>
        /// Gets full normalization details for display in the Playback Information dialog.
        /// </summary>
        public async Task<NormalizationDetails> GetNormalizationDetailsAsync(BaseItemDto item, CancellationToken cancellationToken = default)
        {
            if (item == null) return new NormalizationDetails();

            var prefs = await _preferencesService.GetAppPreferencesAsync().ConfigureAwait(false);
            var details = new NormalizationDetails
            {
                IsEnabled = prefs.EnableVolumeNormalization,
                UseAlbumGain = prefs.UseAlbumGain,
                VolumeOffsetDb = prefs.VolumeOffsetDb
            };

            try
            {
                var itemType = item.GetType();
                details.TrackGainDb = GetNumericValue(itemType.GetProperty("NormalizationGain")?.GetValue(item));
                details.AlbumGainDb = GetNumericValue(itemType.GetProperty("AlbumNormalizationGain")?.GetValue(item));

                if (item.MediaStreams != null && (!details.TrackGainDb.HasValue || !details.AlbumGainDb.HasValue))
                {
                    foreach (var s in item.MediaStreams.Where(s => s.Type == MediaStream_Type.Audio))
                    {
                        var st = s.GetType();
                        if (!details.TrackGainDb.HasValue)
                            details.TrackGainDb = GetNumericValue(st.GetProperty("NormalizationGain")?.GetValue(s));
                        if (!details.AlbumGainDb.HasValue)
                            details.AlbumGainDb = GetNumericValue(st.GetProperty("AlbumNormalizationGain")?.GetValue(s));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error reading normalization properties for details");
            }

            if (prefs.EnableVolumeNormalization)
            {
                details.VolumeMultiplier = await GetVolumeMultiplierAsync(item, cancellationToken).ConfigureAwait(false);
            }

            return details;
        }

        private class GainCacheEntry
        {
            public double? GainDb { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}