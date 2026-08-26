using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Settings;

/// <summary>
/// The framework's <see cref="IEditorSettingsStore"/>, treating <c>settings.json</c> as
/// a trust boundary rather than as its own output read back.
/// </summary>
/// <remarks>
/// <para>
/// The file lives at <c>LocalApplicationData/&lt;ApplicationId&gt;/settings.json</c>. It
/// is user-writable, it may arrive from a roaming profile or a restored backup, and its
/// contents feed paths into the recents menu and the open workflow. Everything in it is
/// hostile input. Concretely:
/// </para>
/// <list type="number">
/// <item><description>
/// The file is opened through <see cref="ISafePathResolver"/>, so a symbolic link, a
/// junctioned ancestor, a FIFO, or a device planted at the settings path is refused
/// before a byte is read.
/// </description></item>
/// <item><description>
/// Size is checked against the handle before parsing, not after. Structure — depth,
/// string length, array length — is checked by a scan that constructs nothing.
/// </description></item>
/// <item><description>
/// Deserialization runs against a source-generated, closed context over one sealed POCO
/// with no polymorphic member. No caller may supply a type resolver, and a document
/// carrying <c>$type</c> is refused on sight.
/// </description></item>
/// <item><description>
/// The schema version selects which code runs and comes from the file, so an unknown or
/// absurd value routes to the malformed path and never to the newest migrator.
/// </description></item>
/// <item><description>
/// Values are then screened individually: paths must be rooted and local, window extents
/// must be plausible and are clamped to the screens that actually exist, and enum-valued
/// settings are parsed by name against the defined set.
/// </description></item>
/// </list>
/// <para>
/// <strong>Two tiers of failure, deliberately.</strong> A structural failure — malformed
/// JSON, a bound exceeded, a type discriminator, an unknown schema version — means the
/// file as a whole cannot be trusted, so it is backed up and replaced with defaults. A
/// value-level failure — one UNC recents entry, an implausible window size, an accent
/// name that is not one of the fourteen — drops or clamps that value and keeps the rest.
/// The plan's own text establishes this split: it says an unknown schema version is
/// malformed, and in the same section says window size is <em>clamped</em>. Wiping a
/// user's theme because one recents entry was bad would be a worse trade than the one
/// the plan already makes, and the sanitization is reported rather than silent.
/// </para>
/// <para>
/// <strong>Fail-soft.</strong> Nothing here blocks startup and nothing here throws for a
/// bad file. This is the opposite of the save workflow, which is fail-loud, and the
/// opposition is intentional: a save that silently does not happen is the worst outcome
/// in this product, and a settings file that silently does not load is a minor one. The
/// two share the hardened path primitive and share no failure policy, because failure
/// policy belongs to the caller.
/// </para>
/// </remarks>
public sealed class EditorSettingsStore : IEditorSettingsStore
{
    /// <summary>Name of the settings file inside the application's settings directory.</summary>
    public const string SettingsFileName = "settings.json";

    private const string BackupPrefix = "settings.corrupt.";
    private const string BackupSuffix = ".json";

    private static readonly Regex BackupGrammar = new(
        @"^settings\.corrupt\.[0-9]{8}T[0-9]{6}Z\.[0-9a-f]{8}\.json$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private readonly EditorSettingsStoreOptions _options;
    private readonly SettingsStructuralLimits _limits;
    private readonly PathResolutionOptions _readOptions;
    private readonly PathResolutionOptions _createOptions;
    private readonly Lock _gate = new();

    private SettingsAnnouncement? _unreported;

    /// <summary>Creates a store for one application identifier.</summary>
    /// <param name="applicationId">Names the settings directory.</param>
    /// <param name="options">Limits and collaborators, or <see langword="null"/> for defaults.</param>
    /// <exception cref="ArgumentException">The identifier was never validated.</exception>
    /// <remarks>
    /// Writability is probed here, once, rather than being discovered on the first save.
    /// A store that reported <see cref="IsPersistent"/> optimistically until something
    /// failed would let the editor accumulate an afternoon of preference changes and
    /// discard all of them at exit without ever having said so.
    /// </remarks>
    public EditorSettingsStore(ApplicationId applicationId, EditorSettingsStoreOptions? options = null)
    {
        if (string.IsNullOrEmpty(applicationId.Value))
        {
            throw new ArgumentException(
                "The application id is uninitialized. Use ApplicationId.Parse or ApplicationId.TryParse; " +
                "a default instance has bypassed validation and must not become a path component.",
                nameof(applicationId));
        }

        _options = options ?? new EditorSettingsStoreOptions();
        ApplicationId = applicationId;

        _limits = new SettingsStructuralLimits(
            _options.MaxDepth,
            _options.MaxStringLength,
            _options.MaxArrayElements);

        _readOptions = new PathResolutionOptions
        {
            Mode = PathResolutionMode.OpenExisting,
            MaxBytes = _options.MaxBackupCopyBytes,
            ConfirmAboveBytes = _options.MaxBackupCopyBytes,
            AllowNonLocalPaths = false,
            ForWriting = false,
        };

        _createOptions = new PathResolutionOptions
        {
            Mode = PathResolutionMode.CreateNew,
            MaxBytes = _options.MaxBackupCopyBytes,
            ConfirmAboveBytes = _options.MaxBackupCopyBytes,
            AllowNonLocalPaths = false,
            ForWriting = true,
        };

        SettingsDirectory = Path.Combine(_options.BaseDirectory, applicationId.Value);
        SettingsFilePath = Path.Combine(SettingsDirectory, SettingsFileName);

        IsPersistent = ProbeWritability(out var probeFailure);

        if (!IsPersistent)
        {
            Announce(new SettingsAnnouncement(
                SettingsLoadOutcome.NotPersistent,
                SettingsRejection.None,
                $"Settings cannot be saved: {probeFailure} The editor is running on in-memory defaults.",
                BackupPath: null));
        }
    }

    /// <summary>The identifier naming the settings directory.</summary>
    public ApplicationId ApplicationId { get; }

    /// <summary>The per-application settings directory.</summary>
    public string SettingsDirectory { get; }

    /// <summary>The settings file.</summary>
    public string SettingsFilePath { get; }

    /// <inheritdoc />
    public bool IsPersistent { get; private set; }

    /// <summary>The most recent status, whether or not it has been reported.</summary>
    public SettingsAnnouncement? Status { get; private set; }

    /// <summary>
    /// Takes the pending announcement, if there is one, clearing it.
    /// </summary>
    /// <param name="announcement">The announcement to surface.</param>
    /// <returns><see langword="true"/> when there was something to say.</returns>
    /// <remarks>
    /// A take-once handoff rather than an event. The requirement is that an unwritable
    /// settings directory is surfaced exactly once — not on every save, which would be
    /// nagging, and not never, which would be the silent-discard failure this exists to
    /// prevent. Take-once makes "exactly once" a property of the store rather than a
    /// convention the shell has to remember.
    /// </remarks>
    public bool TryTakeAnnouncement([NotNullWhen(true)] out SettingsAnnouncement? announcement)
    {
        lock (_gate)
        {
            announcement = _unreported;
            _unreported = null;
            return announcement is not null;
        }
    }

    /// <summary>Creates a lazily-probed recents list over the stored file recents.</summary>
    /// <param name="settings">The loaded settings.</param>
    /// <returns>A recents list. Constructing it probes nothing.</returns>
    public RecentsList CreateFileRecents(EditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new RecentsList(
            settings.RecentFiles,
            EditorSettings.MaxRecentFiles,
            _options.RecentProbe,
            _options.RecentProbeTimeout);
    }

    /// <summary>Creates a lazily-probed recents list over the stored folder recents.</summary>
    /// <param name="settings">The loaded settings.</param>
    /// <returns>A recents list. Constructing it probes nothing.</returns>
    public RecentsList CreateFolderRecents(EditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new RecentsList(
            settings.RecentFolders,
            EditorSettings.MaxRecentFolders,
            _options.RecentProbe,
            _options.RecentProbeTimeout);
    }

    /// <inheritdoc />
    public async ValueTask<EditorSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var defaults = new EditorSettings();

        byte[] bytes;

        var resolution = await _options.PathResolver
            .ResolveAsync(SettingsFilePath, _readOptions, cancellationToken)
            .ConfigureAwait(false);

        switch (resolution)
        {
            case PathResolution.Refused { Reason: PathRefusalReason.NotFound }:
                Announce(new SettingsAnnouncement(
                    SettingsLoadOutcome.NoStoredSettings,
                    SettingsRejection.None,
                    "No settings file exists yet. Defaults are in use.",
                    BackupPath: null),
                    report: false);
                return defaults;

            case PathResolution.Refused refused:
                Announce(new SettingsAnnouncement(
                    SettingsLoadOutcome.Unreadable,
                    SettingsRejection.Unreadable,
                    $"The settings file could not be read and has been left untouched: {refused.Detail}",
                    BackupPath: null));
                return defaults;

            case PathResolution.NeedsConfirmation confirmation:
                // The only condition reachable here is hard-link aliasing, and this is a
                // read: aliasing changes nothing about the bytes. The write path never
                // writes through the alias, it replaces the directory entry.
                using (confirmation.File)
                {
                    bytes = await ReadBoundedAsync(confirmation.File, cancellationToken).ConfigureAwait(false);
                }

                break;

            case PathResolution.Resolved resolved:
                using (resolved.File)
                {
                    bytes = await ReadBoundedAsync(resolved.File, cancellationToken).ConfigureAwait(false);
                }

                break;

            default:
                return defaults;
        }

        var parsed = Interpret(bytes, out var rejection, out var detail, out var sanitized);

        if (parsed is null)
        {
            var backupPath = await BackupAndResetAsync(bytes, cancellationToken).ConfigureAwait(false);

            Announce(new SettingsAnnouncement(
                backupPath is null ? SettingsLoadOutcome.Unreadable : SettingsLoadOutcome.Replaced,
                rejection,
                backupPath is null
                    ? $"The settings file is unusable and could not be backed up: {detail} Defaults are in use."
                    : $"The settings file was unusable and has been replaced with defaults: {detail}",
                backupPath));

            return defaults;
        }

        if (sanitized > 0)
        {
            Announce(new SettingsAnnouncement(
                SettingsLoadOutcome.Sanitized,
                SettingsRejection.None,
                $"{sanitized} stored settings value(s) were rejected or clamped while loading. {detail}".Trim(),
                BackupPath: null));
        }
        else
        {
            Announce(new SettingsAnnouncement(
                SettingsLoadOutcome.Loaded,
                SettingsRejection.None,
                "Settings loaded.",
                BackupPath: null),
                report: false);
        }

        return parsed;
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(EditorSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!IsPersistent)
        {
            return;
        }

        // What is written is what would survive being read back. Persisting a value the
        // reader would reject makes the store the source of its own untrusted input.
        var normalized = Normalize(settings, out _, out _);

        var document = new SettingsDocument
        {
            SchemaVersion = EditorSettings.CurrentSchemaVersion,
            Theme = normalized.Theme.ToString(),
            Accent = normalized.Accent?.ToString(),
            RecentFiles = [.. normalized.RecentFiles],
            RecentFolders = [.. normalized.RecentFolders],
            LastSectionKey = normalized.LastSectionKey,
            WindowWidth = normalized.WindowWidth,
            WindowHeight = normalized.WindowHeight,
        };

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(document, SettingsJsonContext.Default.SettingsDocument);
        }
        catch (Exception ex)
        {
            AnnounceSaveFailure($"the settings could not be serialized ({ex.GetType().Name}: {ex.Message})");
            return;
        }

        await WriteAtomicallyAsync(payload, SettingsFilePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses and screens the raw bytes.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the file must be treated as malformed.
    /// </returns>
    private EditorSettings? Interpret(
        byte[] bytes,
        out SettingsRejection rejection,
        out string detail,
        out int sanitized)
    {
        sanitized = 0;

        if (bytes.LongLength > _options.MaxFileBytes)
        {
            rejection = SettingsRejection.FileTooLarge;
            detail = $"The file is {bytes.LongLength} bytes, past the {_options.MaxFileBytes}-byte maximum.";
            return null;
        }

        rejection = SettingsJsonScanner.Scan(bytes, _limits, out detail);
        if (rejection != SettingsRejection.None)
        {
            return null;
        }

        SettingsDocument? document;
        try
        {
            var reader = new Utf8JsonReader(
                bytes,
                new JsonReaderOptions
                {
                    MaxDepth = _options.MaxDepth,
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                });

            document = JsonSerializer.Deserialize(ref reader, SettingsJsonContext.Default.SettingsDocument);
        }
        catch (Exception ex)
        {
            // Deliberately broad. The set of exception types a hostile document can
            // provoke out of a converter is not a list worth guessing at, and every
            // member of it means the same thing here: the file is not usable and the
            // editor still has to start.
            rejection = SettingsRejection.MalformedJson;
            detail = $"The document did not match the settings schema ({ex.GetType().Name}): {ex.Message}";
            return null;
        }

        if (document is null)
        {
            rejection = SettingsRejection.MalformedJson;
            detail = "The document is empty or null.";
            return null;
        }

        // The version comes from the untrusted file and selects which code runs. Only an
        // exact, known value is routed to a migrator; there is no "newest wins" fallback,
        // because that is precisely the branch an attacker would aim a version number at.
        if (document.SchemaVersion is not EditorSettings.CurrentSchemaVersion)
        {
            rejection = SettingsRejection.UnknownSchemaVersion;
            detail = document.SchemaVersion is null
                ? "The document does not declare a schema version."
                : $"Schema version {document.SchemaVersion.Value} is not a version this build knows how to migrate.";
            return null;
        }

        rejection = SettingsRejection.None;

        var mapped = Map(document, out var mappedSanitized, out var mappedDetail);
        sanitized = mappedSanitized;
        detail = mappedDetail;
        return mapped;
    }

    private EditorSettings Map(SettingsDocument document, out int sanitized, out string detail)
    {
        sanitized = 0;
        var notes = new List<string>();

        var theme = ThemeMode.Dark;
        if (document.Theme is not null)
        {
            if (Enum.TryParse<ThemeMode>(document.Theme, ignoreCase: true, out var parsedTheme) &&
                Enum.IsDefined(parsedTheme))
            {
                theme = parsedTheme;
            }
            else
            {
                sanitized++;
                notes.Add("The stored theme name is not one this build defines.");
            }
        }

        CatppuccinAccent? accent = null;
        if (document.Accent is not null)
        {
            if (Enum.TryParse<CatppuccinAccent>(document.Accent, ignoreCase: true, out var parsedAccent) &&
                Enum.IsDefined(parsedAccent))
            {
                accent = parsedAccent;
            }
            else
            {
                sanitized++;
                notes.Add("The stored accent name is not one of the fourteen.");
            }
        }

        var files = RecentPaths.Normalize(document.RecentFiles, EditorSettings.MaxRecentFiles, out var filesRejected);
        var folders = RecentPaths.Normalize(document.RecentFolders, EditorSettings.MaxRecentFolders, out var foldersRejected);

        if (filesRejected + foldersRejected > 0)
        {
            sanitized += filesRejected + foldersRejected;
            notes.Add($"{filesRejected + foldersRejected} recents entries were not rooted, local, well-formed paths.");
        }

        var sectionKey = document.LastSectionKey;
        if (sectionKey is not null && !IsUsableSectionKey(sectionKey))
        {
            sectionKey = null;
            sanitized++;
            notes.Add("The stored section key was not a usable key.");
        }

        var (width, height, windowSanitized) = ClampWindow(document.WindowWidth, document.WindowHeight);
        if (windowSanitized is not null)
        {
            sanitized++;
            notes.Add(windowSanitized);
        }

        detail = string.Join(" ", notes);

        return new EditorSettings
        {
            SchemaVersion = EditorSettings.CurrentSchemaVersion,
            Theme = theme,
            Accent = accent,
            RecentFiles = files,
            RecentFolders = folders,
            LastSectionKey = sectionKey,
            WindowWidth = width,
            WindowHeight = height,
        };
    }

    /// <summary>
    /// Re-applies every read-side rule to an in-memory value before it is written.
    /// </summary>
    private EditorSettings Normalize(EditorSettings settings, out int sanitized, out string detail)
    {
        sanitized = 0;
        var notes = new List<string>();

        var files = RecentPaths.Normalize(settings.RecentFiles, EditorSettings.MaxRecentFiles, out var filesRejected);
        var folders = RecentPaths.Normalize(settings.RecentFolders, EditorSettings.MaxRecentFolders, out var foldersRejected);
        sanitized += filesRejected + foldersRejected;

        var sectionKey = settings.LastSectionKey;
        if (sectionKey is not null && !IsUsableSectionKey(sectionKey))
        {
            sectionKey = null;
            sanitized++;
        }

        var (width, height, windowSanitized) = ClampWindow(settings.WindowWidth, settings.WindowHeight);
        if (windowSanitized is not null)
        {
            sanitized++;
            notes.Add(windowSanitized);
        }

        detail = string.Join(" ", notes);

        return settings with
        {
            SchemaVersion = EditorSettings.CurrentSchemaVersion,
            Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : ThemeMode.Dark,
            Accent = settings.Accent is { } a && Enum.IsDefined(a) ? a : null,
            RecentFiles = files,
            RecentFolders = folders,
            LastSectionKey = sectionKey,
            WindowWidth = width,
            WindowHeight = height,
        };
    }

    /// <summary>
    /// Clamps a stored window size to a plausible range intersected with the screens
    /// that exist.
    /// </summary>
    /// <remarks>
    /// Negative, zero, non-finite, and implausibly large extents are rejected outright,
    /// leaving the host to choose its own size. Everything else is clamped to the
    /// largest usable area reported by the bounds source. The result therefore cannot be
    /// negative, cannot be zero, and cannot exceed a real screen — none of which the
    /// host has to re-check.
    /// </remarks>
    private (double? Width, double? Height, string? Sanitized) ClampWindow(double? width, double? height)
    {
        if (width is null && height is null)
        {
            return (null, null, null);
        }

        if (width is null || height is null)
        {
            return (null, null, "A stored window size carried only one of its two extents.");
        }

        if (!IsPlausibleExtent(width.Value) || !IsPlausibleExtent(height.Value))
        {
            return (null, null, $"The stored window size {width.Value}x{height.Value} is not a size any window had.");
        }

        var maxWidth = _options.MaxPlausibleWindowExtent;
        var maxHeight = _options.MaxPlausibleWindowExtent;

        var areas = SafeAreas();
        if (areas.Count > 0)
        {
            maxWidth = areas.Max(area => area.Width);
            maxHeight = areas.Max(area => area.Height);
        }

        maxWidth = Math.Max(_options.MinWindowWidth, maxWidth);
        maxHeight = Math.Max(_options.MinWindowHeight, maxHeight);

        var clampedWidth = Math.Clamp(width.Value, _options.MinWindowWidth, maxWidth);
        var clampedHeight = Math.Clamp(height.Value, _options.MinWindowHeight, maxHeight);

        var changed = clampedWidth != width.Value || clampedHeight != height.Value;

        return (
            clampedWidth,
            clampedHeight,
            changed
                ? $"The stored window size {width.Value}x{height.Value} was clamped to {clampedWidth}x{clampedHeight}."
                : null);
    }

    private IReadOnlyList<ScreenArea> SafeAreas()
    {
        try
        {
            var areas = _options.ScreenBounds.GetAvailableAreas();
            if (areas is null)
            {
                return [];
            }

            return [.. areas.Where(area => double.IsFinite(area.Width) && double.IsFinite(area.Height) && area.Width > 0 && area.Height > 0)];
        }
        catch (Exception)
        {
            // A bounds source that throws must not be able to stop settings from loading.
            return [];
        }
    }

    private bool IsPlausibleExtent(double value) =>
        double.IsFinite(value) && value > 0 && value <= _options.MaxPlausibleWindowExtent;

    private static bool IsUsableSectionKey(string key)
    {
        if (key.Length is 0 or > 128)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask<byte[]> ReadBoundedAsync(ResolvedFile file, CancellationToken cancellationToken)
    {
        var stream = file.Stream;

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        var limit = _options.MaxBackupCopyBytes;
        var declared = stream.CanSeek ? stream.Length : limit;
        var capacity = (int)Math.Min(limit + 1, Math.Max(declared, 0));

        using var buffer = new MemoryStream(capacity == 0 ? 1 : capacity);
        var chunk = new byte[64 * 1024];

        while (buffer.Length <= limit)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Preserves the unusable file and writes defaults in its place.
    /// </summary>
    /// <returns>Where the previous file was preserved, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The backup is created with exclusive-create semantics through the same resolver
    /// the save workflow uses, under a name carrying both a UTC timestamp and eight
    /// bytes of entropy. Two malformed startups inside the same second therefore produce
    /// two backups rather than one, which is the whole of finding B9: the previous
    /// implementation shape — one fixed <c>settings.bak</c> — destroys the first good
    /// copy on the second failure, exactly when it is most wanted.
    /// </remarks>
    private async ValueTask<string?> BackupAndResetAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (!IsPersistent)
        {
            return null;
        }

        string? backupPath = null;

        for (var attempt = 0; attempt < 4 && backupPath is null; attempt++)
        {
            var candidate = Path.Combine(SettingsDirectory, BuildBackupName());

            var resolution = await _options.PathResolver
                .CreateNewAsync(candidate, _createOptions, cancellationToken)
                .ConfigureAwait(false);

            switch (resolution)
            {
                case PathResolution.Resolved resolved:
                    try
                    {
                        using (resolved.File)
                        {
                            await resolved.File.Stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                            await resolved.File.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        backupPath = candidate;
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        TryDelete(candidate);
                        return null;
                    }

                    break;

                case PathResolution.Refused { Reason: PathRefusalReason.AlreadyExists }:
                    // Another instance won the name. Draw fresh entropy and try again;
                    // never overwrite what is already there.
                    continue;

                default:
                    return null;
            }
        }

        if (backupPath is null)
        {
            return null;
        }

        PruneBackups();

        await WriteAtomicallyAsync(
            JsonSerializer.SerializeToUtf8Bytes(
                new SettingsDocument { SchemaVersion = EditorSettings.CurrentSchemaVersion, Theme = ThemeMode.Dark.ToString() },
                SettingsJsonContext.Default.SettingsDocument),
            SettingsFilePath,
            cancellationToken).ConfigureAwait(false);

        return backupPath;
    }

    private static string BuildBackupName()
    {
        // No colon: the time separator has to survive a Windows path component, and the
        // path guard refuses a component containing one because it also names an
        // alternate data stream.
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var entropy = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        return $"{BackupPrefix}{stamp}.{entropy}{BackupSuffix}";
    }

    /// <summary>
    /// Applies the retention cap to files matching the framework's own backup grammar.
    /// </summary>
    /// <remarks>
    /// Only the grammar. Anything else sitting in the settings directory was put there by
    /// somebody else, and a retention sweep is not a licence to delete it.
    /// </remarks>
    private void PruneBackups()
    {
        try
        {
            var matching = Directory
                .EnumerateFiles(SettingsDirectory, BackupPrefix + "*" + BackupSuffix)
                .Where(path => BackupGrammar.IsMatch(Path.GetFileName(path)))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(Math.Max(0, _options.BackupRetention))
                .ToList();

            foreach (var stale in matching)
            {
                TryDelete(stale);
            }
        }
        catch (Exception)
        {
            // Retention is housekeeping. Failing it must not fail a load.
        }
    }

    private async ValueTask WriteAtomicallyAsync(byte[] payload, string destination, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(
            SettingsDirectory,
            $"settings.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.tmp");

        var resolution = await _options.PathResolver
            .CreateNewAsync(temp, _createOptions, cancellationToken)
            .ConfigureAwait(false);

        if (resolution is not PathResolution.Resolved resolved)
        {
            var detail = resolution is PathResolution.Refused refused ? refused.Detail : "the temporary file could not be created";
            AnnounceSaveFailure(detail);
            return;
        }

        try
        {
            using (resolved.File)
            {
                await resolved.File.Stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await resolved.File.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                resolved.File.Stream.Flush(flushToDisk: true);
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(temp);
            AnnounceSaveFailure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool ProbeWritability(out string failure)
    {
        failure = string.Empty;

        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var probe = Path.Combine(
                SettingsDirectory,
                $"settings.probe.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.tmp");

            // FileMode.CreateNew is O_CREAT|O_EXCL and CREATE_NEW respectively, so this
            // cannot follow a symbolic link planted at the probe name, and DeleteOnClose
            // removes it on both platforms without a second path lookup.
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            return true;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}.";
            return false;
        }
    }

    private void AnnounceSaveFailure(string detail)
    {
        IsPersistent = false;
        Announce(new SettingsAnnouncement(
            SettingsLoadOutcome.NotPersistent,
            SettingsRejection.None,
            $"Settings could not be saved: {detail} Later changes will not be persisted.",
            BackupPath: null));
    }

    private void Announce(SettingsAnnouncement announcement, bool report = true)
    {
        lock (_gate)
        {
            Status = announcement;

            if (report)
            {
                _unreported = announcement;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
