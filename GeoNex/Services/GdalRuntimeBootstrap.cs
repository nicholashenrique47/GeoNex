using System.Runtime.InteropServices;
using OSGeo.GDAL;
using OSGeo.OGR;

namespace GeoNex.Services;

public static class GdalRuntimeBootstrap
{
    private static readonly object Gate = new();
    private static bool _configured;
    private static bool _ecwReadAvailable;

    public static bool EcwReadAvailable
    {
        get
        {
            Configure();
            return _ecwReadAvailable;
        }
    }

    public static void Configure()
    {
        lock (Gate)
        {
            if (_configured) return;

            if (!OperatingSystem.IsWindows())
            {
                Gdal.AllRegister();
                Ogr.RegisterAll();
                _ecwReadAvailable = Gdal.GetDriverByName("ECW") != null;
                _configured = true;
                return;
            }

            string architecture = Environment.Is64BitProcess ? "x64" : "x86";
            string runtimeRoot = ResolveRuntimeRoot(architecture);
            string nativePath = Path.Combine(runtimeRoot, architecture);
            string pluginPath = Path.Combine(nativePath, "plugins");
            string dataPath = Path.Combine(runtimeRoot, "data");
            string projPath = Path.Combine(runtimeRoot, "share");

            RequireDllDirectory(nativePath, "runtime GDAL");
            PrependToPath(nativePath);
            PrependToPath(pluginPath);

            SetOption("GDAL_DATA", dataPath);
            SetOption("PROJ_DATA", projPath);
            SetOption("PROJ_LIB", projPath);
            SetOption("GDAL_DRIVER_PATH", pluginPath);

            string certificateBundle = Path.Combine(runtimeRoot, "curl-ca-bundle.crt");
            if (File.Exists(certificateBundle)) SetOption("CURL_CA_BUNDLE", certificateBundle);

            try
            {
                // O SDK ECW fica ao lado do plugin e precisa estar no diretório de
                // busca enquanto o GDAL carrega os drivers dinâmicos.
                RequireDllDirectory(pluginPath, "plugins GDAL");
                Gdal.AllRegister();
                Ogr.RegisterAll();
            }
            finally
            {
                RequireDllDirectory(nativePath, "runtime GDAL");
            }

            _ecwReadAvailable = Gdal.GetDriverByName("ECW") != null;
            _configured = true;
            DebugLogger.Log(
                $"GDAL runtime={Gdal.VersionInfo("RELEASE_NAME")} root={runtimeRoot} ecw={_ecwReadAvailable}");
        }
    }

    public static bool IsEcwPath(string? path) =>
        string.Equals(Path.GetExtension(path), ".ecw", StringComparison.OrdinalIgnoreCase);

    public static string ExplainOpenFailure(string path)
    {
        string detail = Gdal.GetLastErrorMsg();
        if (IsEcwPath(path) && !EcwReadAvailable)
            return "O suporte nativo a ECW não foi carregado nesta instalação.";
        return string.IsNullOrWhiteSpace(detail)
            ? "O GDAL não conseguiu abrir o raster; verifique se o arquivo está íntegro."
            : $"O GDAL não conseguiu abrir o raster: {detail}";
    }

    private static string ResolveRuntimeRoot(string architecture)
    {
        string basePath = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(basePath, "gdal"),
            Path.Combine(basePath, "bin"),
            basePath
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, architecture, "gdal.dll")) &&
                File.Exists(Path.Combine(candidate, architecture, "gdal_wrap.dll")))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            $"Runtime GDAL {architecture} não encontrado ao lado do executável.");
    }

    private static void SetOption(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
        Gdal.SetConfigOption(name, value);
    }

    private static void PrependToPath(string directory)
    {
        if (!Directory.Exists(directory)) return;
        string current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(item => string.Equals(item.TrimEnd(Path.DirectorySeparatorChar),
                directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
            return;
        Environment.SetEnvironmentVariable(
            "PATH", directory + Path.PathSeparator + current, EnvironmentVariableTarget.Process);
    }

    private static void RequireDllDirectory(string directory, string description)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Diretório de {description} não encontrado: {directory}");
        if (!SetDllDirectory(directory))
            throw new InvalidOperationException(
                $"Falha ao configurar o diretório de {description} (Win32 {Marshal.GetLastWin32Error()}).");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);
}
