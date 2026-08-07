using System.Globalization;
using System.IO.Abstractions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Scanner.Core.Interfaces.Metadata;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core.Metadata;

public class LocalSubtitlesProvider : ILocalSubtitlesProvider
{
    private static readonly char[] SubtitleSeparatorChars = ['.', '-', '_'];
    private readonly List<CultureInfo> _languageCodes = [];
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILogger<LocalSubtitlesProvider> _logger;
    private readonly IMediaItemRepository _mediaItemRepository;
    private readonly IMetadataRepository _metadataRepository;
    private readonly IFileSystem _fileSystem;

    private readonly SemaphoreSlim _slim = new(1, 1);
    private bool _disposedValue;

    public LocalSubtitlesProvider(
        IMediaItemRepository mediaItemRepository,
        IMetadataRepository metadataRepository,
        IFileSystem fileSystem,
        ILocalFileSystem localFileSystem,
        ILogger<LocalSubtitlesProvider> logger)
    {
        _mediaItemRepository = mediaItemRepository;
        _metadataRepository = metadataRepository;
        _fileSystem = fileSystem;
        _localFileSystem = localFileSystem;
        _logger = logger;
    }

    public async Task<bool> UpdateSubtitles(
        MediaItem mediaItem,
        Option<string> localPath,
        bool saveFullPath,
        CancellationToken cancellationToken)
    {
        if (_languageCodes.Count == 0)
        {
            await _slim.WaitAsync(cancellationToken);
            try
            {
                _languageCodes.AddRange(await _mediaItemRepository.GetAllKnownCultures());
            }
            finally
            {
                _slim.Release();
            }
        }

        if (_languageCodes.Count == 0)
        {
            _logger.LogError("Failed to update subtitles; unable to load languages from database");
            return false;
        }

        Option<ErsatzTV.Core.Domain.Metadata> maybeMetadata = mediaItem switch
        {
            Episode e => e.EpisodeMetadata.OfType<ErsatzTV.Core.Domain.Metadata>().HeadOrNone(),
            Movie m => m.MovieMetadata.OfType<ErsatzTV.Core.Domain.Metadata>().HeadOrNone(),
            MusicVideo mv => mv.MusicVideoMetadata.OfType<ErsatzTV.Core.Domain.Metadata>().HeadOrNone(),
            OtherVideo ov => ov.OtherVideoMetadata.OfType<ErsatzTV.Core.Domain.Metadata>().HeadOrNone(),
            _ => None
        };

        if (maybeMetadata.IsNone)
        {
            _logger.LogError(
                "Failed to update subtitles; unable to load metadata for media item type {Type}",
                mediaItem
                    .GetType().Name);
        }

        foreach (ErsatzTV.Core.Domain.Metadata metadata in maybeMetadata)
        {
            MediaVersion version = mediaItem.GetHeadVersion();
            var subtitleStreams = version.Streams
                .Filter(s => s.MediaStreamKind == MediaStreamKind.Subtitle)
                .ToList();

            var subtitles = subtitleStreams.Map(Subtitle.FromMediaStream).ToList();
            string mediaItemPath = await localPath.IfNoneAsync(() => mediaItem.GetHeadVersion().MediaFiles.Head().Path);
            subtitles.AddRange(LocateExternalSubtitles(_languageCodes, mediaItemPath, saveFullPath));
            bool updateResult = await _metadataRepository.UpdateSubtitles(metadata, subtitles, cancellationToken);
            if (!updateResult)
            {
                _logger.LogError("Failed to save {Count} subtitles to database", subtitles.Count);
            }

            return updateResult;
        }

        return false;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public List<Subtitle> LocateExternalSubtitles(
        List<CultureInfo> languageCodes,
        string mediaItemPath,
        bool saveFullPath)
    {
        var result = new List<Subtitle>();

        string? folder = _fileSystem.Path.GetDirectoryName(mediaItemPath);
        string withoutExtension = _fileSystem.Path.GetFileNameWithoutExtension(mediaItemPath);
        foreach (string file in _localFileSystem.ListFiles(folder, $"{withoutExtension}*"))
        {
            string lowerFile = file.ToLowerInvariant();

            string fileName = _fileSystem.Path.GetFileName(file);
            if (!fileName.StartsWith(withoutExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string extension = _fileSystem.Path.GetExtension(lowerFile);
            string codec = extension switch
            {
                ".ssa" or ".ass" => "ass",
                ".srt" => "subrip",
                ".vtt" => "webvtt",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(codec))
            {
                continue;
            }

            string fileNameWithoutExtension = _fileSystem.Path.GetFileNameWithoutExtension(lowerFile);
            string suffix = GetSubtitleSuffix(fileNameWithoutExtension, withoutExtension);
            string[] tokens = suffix.Split(SubtitleSeparatorChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var forced = tokens.Any(t => t.Equals("forced", StringComparison.OrdinalIgnoreCase));
            var sdh = tokens.Any(t => t.Equals("sdh", StringComparison.OrdinalIgnoreCase) ||
                                      t.Equals("cc", StringComparison.OrdinalIgnoreCase));
            string? languageToken = tokens.FirstOrDefault(t => !IsSubtitleFlag(t));

            // use und when no language is present
            string language = string.IsNullOrWhiteSpace(languageToken)
                ? "und"
                : languageToken;

            Option<CultureInfo> maybeCulture = FindMatchingCulture(languageCodes, language);

            if (maybeCulture.IsNone)
            {
                _logger.LogDebug(
                    "Located {Attribute} with unknown language code {Code} at {Path}",
                    "External Subtitles",
                    language,
                    file);

                result.Add(
                    new Subtitle
                    {
                        SubtitleKind = SubtitleKind.Sidecar,
                        Codec = codec,
                        Default = false,
                        Forced = forced,
                        SDH = sdh,
                        Language = language,
                        Path = saveFullPath ? file : _fileSystem.Path.GetFileName(file),
                        DateAdded = DateTime.UtcNow,
                        DateUpdated = _localFileSystem.GetLastWriteTime(file)
                    });
            }

            foreach (CultureInfo culture in maybeCulture)
            {
                _logger.LogDebug("Located {Attribute} at {Path}", "External Subtitles", file);

                result.Add(
                    new Subtitle
                    {
                        SubtitleKind = SubtitleKind.Sidecar,
                        Codec = codec,
                        Default = false,
                        Forced = forced,
                        SDH = sdh,
                        Language = culture.ThreeLetterISOLanguageName,
                        Path = saveFullPath ? file : _fileSystem.Path.GetFileName(file),
                        DateAdded = DateTime.UtcNow,
                        DateUpdated = _localFileSystem.GetLastWriteTime(file)
                    });
            }
        }


        return result;
    }

    private static string GetSubtitleSuffix(string fileNameWithoutExtension, string withoutExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            return string.Empty;
        }

        string baseName = withoutExtension.ToLowerInvariant();
        if (fileNameWithoutExtension.Equals(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (fileNameWithoutExtension.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
        {
            string suffix = fileNameWithoutExtension[baseName.Length..];
            return suffix.StartsWith('.') || suffix.StartsWith('-') || suffix.StartsWith('_')
                ? suffix[1..]
                : suffix;
        }

        return fileNameWithoutExtension;
    }

    private static bool IsSubtitleFlag(string token) =>
        token.Equals("forced", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("sdh", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("cc", StringComparison.OrdinalIgnoreCase);

    private static Option<CultureInfo> FindMatchingCulture(List<CultureInfo> languageCodes, string language)
    {
        string normalizedLanguage = language.ToLowerInvariant();
        string[] parts = normalizedLanguage.Split(SubtitleSeparatorChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string languageCode = parts.FirstOrDefault() ?? normalizedLanguage;
        string abbreviatedLanguage = languageCode.Length > 3 ? languageCode[..3] : languageCode;

        return languageCodes.Find(ci =>
            ci.TwoLetterISOLanguageName == normalizedLanguage ||
            ci.ThreeLetterISOLanguageName == normalizedLanguage ||
            ci.TwoLetterISOLanguageName == languageCode ||
            ci.ThreeLetterISOLanguageName == languageCode ||
            ci.TwoLetterISOLanguageName == abbreviatedLanguage ||
            ci.ThreeLetterISOLanguageName == abbreviatedLanguage);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _slim.Dispose();
            }

            _disposedValue = true;
        }
    }
}
