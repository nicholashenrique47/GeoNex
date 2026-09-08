using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GeoNex.Services;

internal sealed record BaselineCorpus(
    string Kind,
    int FeatureCount,
    int PointsPerFeature,
    int Columns,
    int RecordBytes);
internal sealed record BaselinePresentation(
    int CssWidth,
    int CssHeight,
    IReadOnlyList<double> DpiScales,
    string Encoding,
    IReadOnlyList<string> InteractionModes);
internal sealed record BaselineScenario(
    string Name,
    double Zoom,
    double TolerancePixels,
    double MicroLodPixels);
internal sealed record BaselineCameraStep(double DeltaX, double DeltaY, double ScaleMultiplier);
internal sealed record EngineBaselineProfile(
    int SchemaVersion,
    string Name,
    int Seed,
    BaselineCorpus Corpus,
    BaselinePresentation Presentation,
    IReadOnlyList<string> CacheStates,
    IReadOnlyList<BaselineScenario> Scenarios,
    IReadOnlyList<BaselineCameraStep> CameraReplay);

internal static class EngineBaseline
{
    private const string ProfileFileName = "engine-baseline.v1.json";

    public static void RunContracts(byte[] shp, long[] offsets)
    {
        EngineBaselineProfile profile = LoadProfile(out string profilePath);
        Validate(profile, shp, offsets);
        string firstReplay = ReplayCamera(profile.CameraReplay);
        string secondReplay = ReplayCamera(profile.CameraReplay);
        Require(firstReplay == secondReplay, "Camera replay is not deterministic.");
        Console.WriteLine(
            $"Engine baseline contracts: PASS (profile={Path.GetFileName(profilePath)}; corpus={offsets.Length}; replay={firstReplay})");
    }

    public static void Capture(byte[] shp, long[] offsets)
    {
        EngineBaselineProfile profile = LoadProfile(out string profilePath);
        Validate(profile, shp, offsets);
        string workspace = FindWorkspaceRoot();
        string outputDirectory = Path.Combine(workspace, "artifacts", "baseline", "latest");
        Directory.CreateDirectory(outputDirectory);
        string metricsPath = Path.Combine(outputDirectory, "pipeline-metrics.json");

        PipelineMetricsReport metrics = PipelineMetrics.Run(shp, offsets, v4: true, webpQuality: 0, metricsPath);
        string nativePath = ResolveNativePath();
        uint nativeAbi = Native.GetGeoNexNativeAbiVersion();
        string managedPath = typeof(EngineBaseline).Assembly.Location;
        string versionedProfilePath = Path.Combine(workspace, "PerfTest", "Baselines", ProfileFileName);
        string profileSha = Sha256File(versionedProfilePath);
        string sourceSha = HashSourceTree(workspace);
        string cameraReplaySha = ReplayCamera(profile.CameraReplay);

        var manifest = new
        {
            schemaVersion = 1,
            capturedAtUtc = DateTimeOffset.UtcNow,
            profile = new { profile.Name, path = Path.GetRelativePath(workspace, versionedProfilePath), sha256 = profileSha },
            source = new
            {
                control = Directory.Exists(Path.Combine(workspace, ".git")) ? "git-present" : "git-not-found",
                treeSha256 = sourceSha,
                hashingRule = "source/config files; excludes bin,obj,artifacts,.vs"
            },
            binaries = new
            {
                managed = new { path = managedPath, sha256 = Sha256File(managedPath) },
                native = new
                {
                    path = nativePath,
                    abi = nativeAbi,
                    expectedAbi = NativeMethods.ExpectedAbiVersion,
                    sha256 = Sha256File(nativePath)
                }
            },
            environment = new
            {
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
                logicalCpuCount = Environment.ProcessorCount,
                availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
            },
            corpus = profile.Corpus,
            presentation = profile.Presentation,
            cacheStates = profile.CacheStates,
            scenarios = profile.Scenarios,
            cameraReplay = new { steps = profile.CameraReplay.Count, finalStateSha256 = cameraReplaySha },
            measurement = new
            {
                samples = metrics.Samples,
                warmupPasses = metrics.WarmupPasses,
                coldStartRecorded = true,
                metricsFile = "pipeline-metrics.json",
                metricsSha256 = Sha256File(metricsPath)
            }
        };

        string manifestPath = Path.Combine(outputDirectory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        Console.WriteLine($"Engine baseline manifest: {manifestPath}");
        Console.WriteLine($"Source SHA256: {sourceSha}");
        Console.WriteLine($"Native ABI/SHA256: {nativeAbi}/{Sha256File(nativePath)}");
    }

    private static EngineBaselineProfile LoadProfile(out string profilePath)
    {
        profilePath = Path.Combine(AppContext.BaseDirectory, "Baselines", ProfileFileName);
        if (!File.Exists(profilePath))
            throw new FileNotFoundException("Versioned engine baseline profile was not copied to output.", profilePath);
        return JsonSerializer.Deserialize<EngineBaselineProfile>(File.ReadAllText(profilePath), JsonOptions)
            ?? throw new InvalidDataException("Engine baseline profile is empty.");
    }

    private static void Validate(EngineBaselineProfile profile, byte[] shp, long[] offsets)
    {
        Require(profile.SchemaVersion == 1, "Unsupported baseline schema.");
        Require(profile.Corpus.FeatureCount == offsets.Length, "Baseline feature count differs from generated corpus.");
        Require(profile.Corpus.PointsPerFeature == 9, "Baseline vertex count differs from generated corpus.");
        int actualRecordBytes = checked((shp.Length - 100) / offsets.Length);
        Require(profile.Corpus.RecordBytes == actualRecordBytes, "Baseline SHP record size differs from generated corpus.");
        Require(profile.Presentation.CssWidth == 1920 && profile.Presentation.CssHeight == 1080, "Pipeline viewport is not declared correctly.");
        Require(profile.Presentation.DpiScales.SequenceEqual(new[] { 1d, 1.25d, 1.5d, 2d, 4d }), "DPI matrix changed unexpectedly.");
        Require(profile.CacheStates.Contains("cold", StringComparer.Ordinal) && profile.CacheStates.Contains("warm", StringComparer.Ordinal), "Cold/warm cache states must be declared.");
        Require(profile.Scenarios.Select(value => value.Name).SequenceEqual(new[] { "detail", "overview", "exact" }), "The three priority scenarios changed.");
        Require(profile.CameraReplay.Count >= 3, "Camera replay is too small.");
        _ = ReplayCamera(profile.CameraReplay);
    }

    private static string ReplayCamera(IReadOnlyList<BaselineCameraStep> steps)
    {
        var state = new MapCameraState(0, 0, 1);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (BaselineCameraStep step in steps)
        {
            if (!MapCoordinateSpace.TryApplyCssCameraDelta(
                state,
                step.DeltaX,
                step.DeltaY,
                step.ScaleMultiplier,
                out state))
                throw new InvalidDataException("Baseline camera replay contains an invalid step.");
            hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"{state.PanX:R}|{state.PanY:R}|{state.Zoom:R}\n")));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ResolveNativePath()
    {
        string? configured = Environment.GetEnvironmentVariable("GEONEX_NATIVE_PATH");
        string path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "GeoNexNative.dll")
            : configured;
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Native baseline binary was not found.", path);
        return path;
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GeoNex.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("GeoNex workspace root was not found.");
    }

    private static string HashSourceTree(string workspace)
    {
        string[] extensions = [".cs", ".csproj", ".cpp", ".h", ".razor", ".js", ".json", ".slnx", ".props", ".targets", ".bat"];
        string[] excludedSegments = ["bin", "obj", "artifacts", ".vs"];
        string[] files = Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !Path.GetRelativePath(workspace, path).Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => excludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(path => Path.GetRelativePath(workspace, path), StringComparer.Ordinal)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] separator = [0];
        byte[] buffer = new byte[64 * 1024];
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(workspace, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(separator);
            using FileStream stream = File.OpenRead(file);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer.AsSpan(0, read));
            hash.AppendData(separator);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
