using System.Text.RegularExpressions;
using PS5PKGTool.Core.Builders;
using PS5PKGTool.Core.Parsers;
using PS5PKGTool.Core.Services;
using ProsperoPkgTool.Containers;

namespace PS5PKGTool.Core.Backends;

/// <summary>
/// The LibProsperoPkg backend, using the vendored <b>1.2.0</b> build (the version the working
/// PPR-PKG/fpkg-gui ships) loaded in an isolated context by <see cref="LibProsperoPkg12"/>. Unlike
/// the newer 2.6.0, 1.2.0 lays out large titles correctly and exposes SDK, compression, Kraken,
/// PlayGo, deterministic and workspace options. Builds run from an unpacked folder; image sources
/// are not supported. The DRM type is applied by patching the source sce_sys/param.json (1.2.0 has
/// no DRM option), restored afterwards.
/// </summary>
public sealed class LppBackend : IPackageBackend
{
    public const string BackendId = "lpp";

    public string Id => BackendId;

    public string DisplayName => "LibProsperoPkg 1.2.0";

    public BackendCapabilities Capabilities { get; } = new(
        BuildFromDirectory: true,
        BuildFromImage: false,
        Validate: true,
        Extract: true,
        SupportsLargeImages: true,
        SupportsSeed: true,
        SupportsCompressionKnobs: true,
        SupportsRightSprxToggle: false,
        SupportsPlayGoChunks: true);

    public Task<SonyDebugPackageBuildResult> BuildFromDirectoryAsync(string sourceDirectory, string outputPath,
        SonyDebugPackageBuildOptions options, IProgress<SonyDebugPackageProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => BuildFromDirectory(sourceDirectory, outputPath, options, progress, cancellationToken),
            cancellationToken);

    public Task<SonyDebugPackageBuildResult> BuildFromImageAsync(string imagePath, string outputPath,
        SonyDebugPackageBuildOptions options, IProgress<SonyDebugPackageProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new BackendNotSupportedException(BackendId,
            "LibProsperoPkg cannot build a package from an image source. " +
            "Use ProsperoPkgTool for exFAT/FFPKG/FFPFSC sources.");

    public SonyDebugPackageValidationResult Validate(string packagePath,
        string passcode = SonyDebugPackageCredentials.DefaultPasscode)
    {
        (bool valid, string message) = LibProsperoPkg12.Validate(packagePath);
        return new SonyDebugPackageValidationResult
        {
            IsValid = valid,
            Message = message,
            IndexedFiles = 0,
            PackageSize = File.Exists(packagePath) ? new FileInfo(packagePath).Length : 0,
        };
    }

    public async Task<SonyPackageExtractResult> ExtractAsync(string packagePath, string destination,
        string passcode = SonyDebugPackageCredentials.DefaultPasscode,
        IProgress<SonyPackageExtractProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string output = Path.GetFullPath(destination);
        Directory.CreateDirectory(output);
        long packageBytes = File.Exists(packagePath) ? new FileInfo(packagePath).Length : 0;
        long estimatedInnerBytes = TryEstimateInnerBytes(packagePath, passcode);

        progress?.Report(new SonyPackageExtractProgress(
            0, packageBytes, string.Empty, "Decrypt package", 0d));

        Task<int> extraction = Task.Run(() => LibProsperoPkg12.Extract(packagePath, output, passcode),
            CancellationToken.None);
        using var monitorCts = new CancellationTokenSource();
        Task monitor = MonitorExtractionAsync(output, packageBytes, estimatedInnerBytes, progress, extraction,
            monitorCts.Token);

        try
        {
            int files = await extraction.ConfigureAwait(false);
            (long extractedBytes, int extractedFiles, string lastFile) = MeasureExtractedTree(output);
            progress?.Report(new SonyPackageExtractProgress(
                extractedBytes, extractedBytes, lastFile, "Write game files", 1d,
                extractedFiles, Math.Max(files, extractedFiles)));

            return new SonyPackageExtractResult
            {
                Destination = output,
                FileCount = files,
                ExtractedBytes = extractedBytes,
            };
        }
        finally
        {
            monitorCts.Cancel();
            try { await monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task MonitorExtractionAsync(string output, long packageBytes, long estimatedInnerBytes,
        IProgress<SonyPackageExtractProgress>? progress, Task extraction, CancellationToken cancellationToken)
    {
        if (progress is null)
            return;

        string lastStage = string.Empty;
        long lastBytes = -1;
        int lastItems = -1;

        while (!extraction.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ExtractionTemps temps = FindExtractionTemps(output);
            string stage;
            long current;
            long total;
            int currentItems = 0;
            string currentPath = string.Empty;
            double stagePercent = -1d;

            if (temps.DecodingInner is not null)
            {
                stage = "Decode inner image";
                current = SafeLength(temps.DecodingInner);
                total = estimatedInnerBytes;
                currentPath = Path.GetFileName(temps.DecodingInner);
                if (total > 0)
                    stagePercent = Math.Clamp((double)current / total, 0d, 0.995d);
            }
            else if (temps.InnerImage is not null)
            {
                stage = "Write game files";
                long innerBytes = SafeLength(temps.InnerImage);
                (current, currentItems, currentPath) = MeasureExtractedTree(output);
                total = innerBytes > 0 ? innerBytes : estimatedInnerBytes;
                if (total > 0)
                    stagePercent = Math.Clamp((double)current / total, 0d, 0.995d);
            }
            else
            {
                stage = "Decrypt package";
                current = temps.OuterImage is null ? 0 : SafeLength(temps.OuterImage);
                total = packageBytes;
                currentPath = temps.OuterImage is null ? string.Empty : Path.GetFileName(temps.OuterImage);
                if (total > 0)
                    stagePercent = Math.Clamp((double)current / total, 0d, 0.995d);
            }

            if (!string.Equals(stage, lastStage, StringComparison.Ordinal) ||
                current != lastBytes || currentItems != lastItems)
            {
                progress.Report(new SonyPackageExtractProgress(
                    current, total, currentPath, stage, stagePercent, currentItems, 0));
                lastStage = stage;
                lastBytes = current;
                lastItems = currentItems;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
        }
    }

    private static long TryEstimateInnerBytes(string packagePath, string passcode)
    {
        try
        {
            var pfs = new SonyPkgReader().Read(packagePath, passcode).NestedPfs;
            if (pfs is null || pfs.BlockSize == 0 || pfs.DataBlockCount <= 0)
                return 0;
            return checked((long)pfs.BlockSize * pfs.DataBlockCount);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or
                                   UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private readonly record struct ExtractionTemps(string? OuterImage, string? DecodingInner, string? InnerImage);

    private static ExtractionTemps FindExtractionTemps(string output)
    {
        string? outer = null;
        string? decoding = null;
        string? inner = null;
        try
        {
            foreach (string path in Directory.EnumerateFiles(output, ".*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("..libprospero-inner-", StringComparison.OrdinalIgnoreCase))
                    decoding = path;
                else if (name.StartsWith(".libprospero-inner-", StringComparison.OrdinalIgnoreCase))
                    inner = path;
                else if (name.StartsWith(".libprospero-outer-", StringComparison.OrdinalIgnoreCase))
                    outer = path;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new ExtractionTemps(outer, decoding, inner);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static (long Bytes, int Files, string CurrentPath) MeasureExtractedTree(string output)
    {
        long bytes = 0;
        int files = 0;
        string currentPath = string.Empty;
        DateTime currentWrite = DateTime.MinValue;

        try
        {
            foreach (string path in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(output, path);
                if (!relative.Contains(Path.DirectorySeparatorChar) &&
                    Path.GetFileName(path).StartsWith(".libprospero-", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!relative.Contains(Path.DirectorySeparatorChar) &&
                    Path.GetFileName(path).StartsWith("..libprospero-inner-", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var info = new FileInfo(path);
                    bytes += info.Length;
                    files++;
                    if (info.LastWriteTimeUtc > currentWrite)
                    {
                        currentWrite = info.LastWriteTimeUtc;
                        currentPath = relative;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return (bytes, files, currentPath);
    }

    private static SonyDebugPackageBuildResult BuildFromDirectory(string sourceDirectory, string outputPath,
        SonyDebugPackageBuildOptions options, IProgress<SonyDebugPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(sourceDirectory);
        string destination = Path.GetFullPath(outputPath);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);

        SonyDebugPackageCredentials credentials =
            SonyDebugPackageCredentials.Create(options.ContentId, options.Passcode);
        (long sourceBytes, int sourceFiles) = MeasureTree(source);

        progress?.Report(new SonyDebugPackageProgress(
            "Building (LibProsperoPkg)", 0, 0, Path.GetFileName(source)));

        // LibProsperoPkg 1.2.0 uses an existing param.json verbatim, so a DRM override is applied by
        // patching sce_sys/param.json. To keep the original dump untouched the build runs from a
        // same-volume hardlink overlay whose param.json is a real patched copy; only if that overlay
        // cannot be created do we fall back to a crash-recoverable in-place patch.
        string paramPath = Path.Combine(source, "sce_sys", "param.json");
        string buildSource = source;
        string? drmOverlay = TryCreateDrmOverlay(source, paramPath, options);
        byte[]? originalParam = null;
        if (drmOverlay is not null)
            buildSource = drmOverlay;
        else
            originalParam = ApplyDrmPatch(paramPath, options);

        string produced;
        try
        {
            var translator = new LppProgressTranslator(progress);
            produced = LibProsperoPkg12.Build(new LibProsperoPkg12BuildRequest
            {
                SourceFolder = buildSource,
                OutputFolder = Path.GetDirectoryName(destination) ?? Directory.GetCurrentDirectory(),
                ContentId = credentials.ContentId,
                Passcode = credentials.Passcode,
                SdkVersion = options.SdkVersionOverride,
                PlayGoChunks = options.PlayGoChunkCount,
                Deterministic = options.Seed is not null,
                OuterSeed = options.Seed,
                Compression = options.Compression == Ps5InnerCompression.Stored ? 0 : 2,
                KrakenLevel = options.KrakenLevel,
                KrakenThreads = options.KrakenThreads,
                TempDirectory = options.TempDirectory,
                Log = message =>
                {
                    options.Log?.Invoke(message);
                    translator.OnLog(message);
                },
            });
        }
        catch (Exception ex) when (IsUnsupportedLayout(ex))
        {
            throw new BackendNotSupportedException(BackendId,
                "LibProsperoPkg cannot lay out this title ('" + ex.Message + "'). " +
                "Switch the builder to ProsperoPkgTool and retry.");
        }
        finally
        {
            if (drmOverlay is not null)
                TryDeleteDirectory(drmOverlay);
            if (originalParam is not null)
            {
                try
                {
                    RestoreDrmPatch(paramPath, originalParam);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Never swallow this: keep the backup on disk so the next build restores the dump.
                    options.Log?.Invoke("Warning: could not restore " + paramPath + " (" + ex.Message +
                        "). The original is kept as " + Path.GetFileName(paramPath) + ParamBackupSuffix +
                        " and will be restored automatically on the next build.");
                }
            }
        }

        if (!string.Equals(Path.GetFullPath(produced), destination, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(produced, destination);
        }

        return new SonyDebugPackageBuildResult
        {
            OutputPath = destination,
            ContentId = credentials.ContentId,
            KeyFingerprint = credentials.Fingerprint,
            PackageSize = new FileInfo(destination).Length,
            SourceBytes = sourceBytes,
            SourceFiles = sourceFiles,
            UsesDefaultPasscode = credentials.UsesDefaultPasscode,
        };
    }

    private static (long Bytes, int Files) MeasureTree(string root)
    {
        long bytes = 0;
        int files = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(path).Length;
            files++;
        }
        return (bytes, files);
    }

    private const string ParamBackupSuffix = ".ps5pkgtool-backup";

    /// <summary>
    /// LibProsperoPkg 1.2.0 has no DRM option and uses the source <c>sce_sys/param.json</c>
    /// verbatim, so a DRM override is applied by patching that file. Before patching, the original
    /// bytes are written to a sibling <c>param.json.ps5pkgtool-backup</c> so an interrupted or
    /// crashed build can be recovered: any leftover backup is restored before the next patch.
    /// Returns the original bytes when a patch was applied, otherwise null.
    /// </summary>
    private static byte[]? ApplyDrmPatch(string paramPath, SonyDebugPackageBuildOptions options)
    {
        if (!File.Exists(paramPath))
            return null;

        // Recover a patch left behind by a build that was killed before its finally block ran.
        RecoverLeftoverPatch(paramPath);
        if (string.IsNullOrWhiteSpace(options.DrmTypeOverride))
            return null;

        byte[] original = File.ReadAllBytes(paramPath);
        byte[] updated = ProsperoPkgTool.Containers.ProsperoPublisherParamJson.ApplyDrmType(original, options.DrmTypeOverride);
        if (updated.AsSpan().SequenceEqual(original))
            return null;

        File.WriteAllBytes(paramPath + ParamBackupSuffix, original);
        File.WriteAllBytes(paramPath, updated);
        return original;
    }

    private static void RestoreDrmPatch(string paramPath, byte[]? original)
    {
        if (original is null)
            return;
        File.WriteAllBytes(paramPath, original);
        string backup = paramPath + ParamBackupSuffix;
        if (File.Exists(backup)) File.Delete(backup);
    }

    private static void RecoverLeftoverPatch(string paramPath)
    {
        string backup = paramPath + ParamBackupSuffix;
        if (!File.Exists(backup))
            return;
        File.Copy(backup, paramPath, overwrite: true);
        File.Delete(backup);
    }

    /// <summary>
    /// Builds a same-volume hardlink mirror of the source with a real, patched <c>param.json</c>, so a
    /// DRM override never modifies the original dump. Returns the overlay root, or null when no patch
    /// is needed or the overlay cannot be created (the caller then patches in place with recovery).
    /// </summary>
    private static string? TryCreateDrmOverlay(string source, string paramPath, SonyDebugPackageBuildOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DrmTypeOverride) || !File.Exists(paramPath))
            return null;

        byte[] original = File.ReadAllBytes(paramPath);
        byte[] updated = ProsperoPkgTool.Containers.ProsperoPublisherParamJson.ApplyDrmType(original, options.DrmTypeOverride);
        if (updated.AsSpan().SequenceEqual(original))
            return null;

        string trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string parent = Path.GetDirectoryName(trimmed) is { Length: > 0 } directory
            ? directory
            : Path.GetPathRoot(trimmed)!;
        string overlay = Path.Combine(parent, ".ps5pkgtool-drm-" + Guid.NewGuid().ToString("N"));
        try
        {
            MirrorTree(source, overlay);
            string overlayParam = Path.Combine(overlay, Path.GetRelativePath(source, paramPath));
            File.Delete(overlayParam);
            File.WriteAllBytes(overlayParam, updated);
            options.Log?.Invoke(
                "Applying the DRM override through a linked build overlay; the source dump is left untouched.");
            return overlay;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            options.Log?.Invoke("Could not create a linked build overlay (" + ex.Message +
                "); applying the DRM override in place with crash recovery instead.");
            TryDeleteDirectory(overlay);
            return null;
        }
    }

    private static void MirrorTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source))
            MirrorTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string destination = Path.Combine(target, Path.GetFileName(file));
            if (CreateHardLink(destination, file, IntPtr.Zero))
                continue;
            File.Copy(file, destination); // different volume or a filesystem without hardlinks
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newLink, string existingFile, IntPtr securityAttributes);

    private static bool IsUnsupportedLayout(Exception ex)
    {
        while (ex is System.Reflection.TargetInvocationException { InnerException: { } inner })
            ex = inner;
        // Prefer the engine's own classification; keep the legacy message checks only as a fallback
        // for older vendored builds that predate ProsperoErrorInfo.
        if (ProsperoErrorInfo.IsUnsupported(ex))
            return true;
        return ex is NotSupportedException or OverflowException ||
            ex.Message.Contains("single-byte field", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("this layout size is not supported", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns the LibProsperoPkg build log into stage/percent progress reports so the task queue's
    /// steps and progress bar advance. Each line is matched for a <c>[stage N/5]</c> marker (which
    /// maps to the task step) and a <c>data NN%</c> / <c>: NN%</c> percentage; reports are emitted
    /// only when the stage or percentage changes.
    /// </summary>
    private sealed class LppProgressTranslator
    {
        // Stage strings chosen so they contain the task plan's keywords.
        private static readonly string[] StageNames =
        [
            "Reading source folder", // 0 - before the first stage marker
            "Inner image",           // stage 1/5
            "NAPS",                  // stage 2/5
            "Outer PFS",             // stage 3/5
            "CNT",                   // stage 4/5
            "Finalize",              // stage 5/5
        ];

        private static readonly Regex StageRegex = new(@"\[stage\s+(\d+)/\d+\]", RegexOptions.Compiled);
        private static readonly Regex PercentRegex = new(@"(?:data\s+|:\s+)(\d{1,3})%", RegexOptions.Compiled);

        private readonly IProgress<SonyDebugPackageProgress>? _progress;
        private string _stage = StageNames[0];
        private int _percent = -1;

        public LppProgressTranslator(IProgress<SonyDebugPackageProgress>? progress)
        {
            _progress = progress;
            _progress?.Report(new SonyDebugPackageProgress(_stage, 0, 100, string.Empty));
        }

        public void OnLog(string line)
        {
            if (_progress is null || string.IsNullOrEmpty(line))
                return;

            bool stageChanged = false;
            Match stage = StageRegex.Match(line);
            if (stage.Success && int.TryParse(stage.Groups[1].Value, out int number) &&
                number >= 0 && number < StageNames.Length)
            {
                stageChanged = !string.Equals(_stage, StageNames[number], StringComparison.Ordinal);
                _stage = StageNames[number];
            }
            else if (line.Contains("source tree scan", StringComparison.OrdinalIgnoreCase))
            {
                stageChanged = !string.Equals(_stage, StageNames[0], StringComparison.Ordinal);
                _stage = StageNames[0];
            }

            int percent = _percent < 0 ? 0 : _percent;
            Match match = PercentRegex.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed))
                percent = Math.Clamp(parsed, 0, 100);
            else if (line.Contains("complete", StringComparison.OrdinalIgnoreCase))
                percent = 100;

            if (!stageChanged && percent == _percent)
                return;
            _percent = percent;
            _progress.Report(new SonyDebugPackageProgress(_stage, percent, 100, string.Empty));
        }
    }
}
