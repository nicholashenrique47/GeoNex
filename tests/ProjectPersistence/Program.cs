using GeoNex.Data;
using GeoNex.Models;
using GeoNex.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Runtime.InteropServices;

var root = Path.Combine(Path.GetTempPath(), "GeoNexProjectPersistenceTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await VerifyCreateSaveReopenAsync(root);
    await VerifyRecoveryPointBeforeExplicitSaveAsync(root);
    await VerifyAutosaveStaysSeparateAndPromotesAsync(root);
    await VerifyCreateDoesNotOverwriteExistingAsync(root);
    await VerifyExtensionValidationAsync(root);
    await VerifySaveRollbackAsync(root);
    await VerifyDuplicateSaveRejectedWithoutMutationAsync(root);
    await VerifyLegacyMigrationAsync(root);
    await VerifyInvalidAndFutureProjectsAsync(root);
    await VerifyDuplicateLayerValidationAsync(root);
    await VerifyDistinctLayerNamesWithSharedSourceBasenameAsync(root);
    VerifyPathResolution(root);
    await VerifyMovedProjectResolvesRelativeSourcesAsync(root);
    await VerifyProjectSourceValidationAsync(root);
    Console.WriteLine("PASS: Project persistence, schema migration, validation, and path contracts.");
}

finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static async Task VerifyAutosaveStaysSeparateAndPromotesAsync(string root)
{
    string path = Path.Combine(root, "autosave", "survey.gnx");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string layerPath = Path.Combine(Path.GetDirectoryName(path)!, "roads.geojson");
    await File.WriteAllTextAsync(layerPath, "{}");
    Projeto project = await GnxProjectStore.CriarAsync(path, "Autosave survey");
    var original = new CamadaProjetoSnapshot("roads", "Vetor", true, 0, layerPath,
        "{\"Tipo\":\"Arquivo\"}", "manual-style");
    await GnxProjectStore.SalvarAsync(project, [original], "EPSG:4326", null, 0, 0, false,
        0, 0, 1, "{\"layout\":\"manual\"}");

    string autosavePath = GnxProjectStore.ObterCaminhoAutosave(path);
    bool unchanged = await GnxProjectStore.SalvarAutosaveAsync(project, [original], "EPSG:4326", null,
        0, 0, false, 0, 0, 1, "{\"layout\":\"manual\"}");
    Assert(!unchanged && !File.Exists(autosavePath), "unchanged projects do not create false crash-recovery candidates");

    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));
    var automatic = new CamadaProjetoSnapshot("roads", "Vetor", false, 0, layerPath,
        "{\"Tipo\":\"Arquivo\"}", "automatic-style");
    bool updated = await GnxProjectStore.SalvarAutosaveAsync(project, [automatic], "EPSG:31982", "roads",
        5, 6, true, 7, 8, 2, "{\"layout\":\"automatic\"}");
    Assert(updated && File.Exists(autosavePath), "changed sessions are written to a separate autosave document");
    Assert(GnxProjectStore.TemAutosaveMaisRecente(path), "newer autosave is offered for recovery");

    bool reverted = await GnxProjectStore.SalvarAutosaveAsync(project, [original], "EPSG:4326", null,
        0, 0, false, 0, 0, 1, "{\"layout\":\"manual\"}");
    Assert(!reverted && !File.Exists(autosavePath), "returning to the saved state removes a stale recovery candidate");
    updated = await GnxProjectStore.SalvarAutosaveAsync(project, [automatic], "EPSG:31982", "roads",
        5, 6, true, 7, 8, 2, "{\"layout\":\"automatic\"}");
    Assert(updated && File.Exists(autosavePath), "a later change can create a fresh recovery candidate");

    var later = new CamadaProjetoSnapshot("roads", "Vetor", true, 0, layerPath,
        "{\"Tipo\":\"Arquivo\"}", "latest-style");
    bool updatedExisting = await GnxProjectStore.SalvarAutosaveAsync(project, [later], "EPSG:31984", "roads",
        9, 10, true, 11, 12, 3, "{\"layout\":\"latest\"}");
    Assert(updatedExisting, "subsequent autosaves replace the recoverable session transactionally");

    Projeto manualBeforeRecovery = await GnxProjectStore.AbrirAsync(path);
    Projeto snapshot = await GnxProjectStore.AbrirAsync(autosavePath);
    Assert(manualBeforeRecovery.CRS == "EPSG:4326" && manualBeforeRecovery.Camadas.Single().EstiloJson == "manual-style",
        "background autosave does not mutate the last manually saved project");
    Assert(snapshot.Id == project.Id && snapshot.CRS == "EPSG:31984" && snapshot.Camadas.Single().Visivel &&
        snapshot.Camadas.Single().EstiloJson == "latest-style" && snapshot.LayoutJson == "{\"layout\":\"latest\"}",
        "autosave stores full session state under the original project identity");

    Projeto recovered = await GnxProjectStore.PromoverAutosaveAsync(path);
    Projeto recoveryPoint = await GnxProjectStore.AbrirAsync(GnxProjectStore.ObterCaminhoPontoRestauracao(path));
    Assert(recovered.CRS == "EPSG:31984" && recovered.CameraZoom == 3 &&
        recovered.Camadas.Single().EstiloJson == "latest-style" && recovered.LayoutJson == "{\"layout\":\"latest\"}",
        "explicit recovery promotes all autosaved project state to the original document");
    Assert(recoveryPoint.CRS == "EPSG:4326" && recoveryPoint.Camadas.Single().EstiloJson == "manual-style" &&
        recoveryPoint.LayoutJson == "{\"layout\":\"manual\"}",
        "promotion preserves the last manual version as a recovery point");
    Assert(File.Exists(autosavePath) && !GnxProjectStore.TemAutosaveMaisRecente(path),
        "autosave remains until successful map activation and is no longer considered newer after promotion");

    GnxProjectStore.DescartarAutosave(path);
    Assert(!File.Exists(autosavePath), "successful recovery can discard its consumed autosave");
}

static async Task VerifyRecoveryPointBeforeExplicitSaveAsync(string root)
{
    string path = Path.Combine(root, "recovery", "survey.gnx");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    Projeto project = await GnxProjectStore.CriarAsync(path, "Survey");
    var previous = new CamadaProjetoSnapshot("roads", "Vetor", true, 0, string.Empty,
        "{\"Tipo\":\"Arquivo\"}", "previous-style");
    await GnxProjectStore.SalvarAsync(project, [previous], "EPSG:4326", null, 0, 0, false,
        0, 0, 1, null);
    string recoveryPath = GnxProjectStore.ObterCaminhoPontoRestauracao(path);
    Assert(!File.Exists(recoveryPath), "automatic/background saves can avoid full recovery snapshots");

    var updated = new CamadaProjetoSnapshot("roads", "Vetor", false, 0, string.Empty,
        "{\"Tipo\":\"Arquivo\"}", "updated-style");
    await GnxProjectStore.SalvarAsync(project, [updated], "EPSG:31982", "roads", 10, 20, true,
        30, 40, 2, "updated-layout", criarPontoRestauracao: true);

    Assert(File.Exists(recoveryPath), "explicit save creates a recovery project beside the original");
    Projeto recovery = await GnxProjectStore.AbrirAsync(recoveryPath);
    Assert(recovery.CRS == "EPSG:4326" && recovery.Camadas.Single().EstiloJson == "previous-style",
        "recovery point contains the complete project state from before the explicit save");
    Assert(recovery.LayoutJson is null && recovery.CameraZoom == 1,
        "recovery point preserves prior layout and camera settings");

    Projeto current = await GnxProjectStore.AbrirAsync(path);
    Assert(current.CRS == "EPSG:31982" && current.Camadas.Single().EstiloJson == "updated-style" &&
        current.LayoutJson == "updated-layout" && current.CameraZoom == 2,
        "creating a recovery point does not prevent the requested save");
    Assert(GnxProjectStore.EhPontoRestauracao(recoveryPath), "recovery project is identifiable by its reserved suffix");
}

static async Task VerifyCreateSaveReopenAsync(string root)
{
    string projectDirectory = Path.Combine(root, "portable", "Map; Project");
    string projectPath = Path.Combine(projectDirectory, "map.gnx");
    string nestedSource = Path.Combine(projectDirectory, "data", "roads.shp");
    string externalSource = Path.Combine(root, "external", "ortho.tif");
    Directory.CreateDirectory(Path.GetDirectoryName(nestedSource)!);
    Directory.CreateDirectory(Path.GetDirectoryName(externalSource)!);
    await File.WriteAllTextAsync(nestedSource, "vector fixture");
    await File.WriteAllTextAsync(externalSource, "raster fixture");

    Projeto project = await GnxProjectStore.CriarAsync(projectPath, "Regional plan");
    var postgisSource = new ProjetoFonteCamada
    {
        Tipo = "PostGIS", Servidor = "gis.example.test", Porta = 5432, BancoDados = "regional",
        Usuario = "map_reader", ModoSsl = "Require", Esquema = "public", Tabela = "parcels",
        ColunaGeometria = "geom", TipoGeometria = "MULTIPOLYGON", Srid = 31982, DimensaoCoordenada = 2
    };
    string postgisJson = System.Text.Json.JsonSerializer.Serialize(postgisSource);
    var roadsStyleJson = System.Text.Json.JsonSerializer.Serialize(new EstiloCamada
    {
        TipoSimbologia = "CATEGORIZADA",
        ColunaSimbologia = "zone",
        CoresCategorizadas = new Dictionary<string, string>
        {
            ["residential"] = "#2a5",
            ["industrial"] = "#b50"
        },
        ExibirRotulos = true,
        ColunaRotulo = "street_name",
        CorTextoRotulo = "#ffffff",
        CorHaloRotulo = "#102030",
        TamanhoTextoRotulo = 14,
        TamanhoHaloRotulo = 4,
        AntiColisaoRotulos = false
    });
    var rows = new[]
    {
        new CamadaProjetoSnapshot("roads", "Vetor", false, 0, nestedSource,
            "{\"Tipo\":\"Arquivo\"}", roadsStyleJson),
        new CamadaProjetoSnapshot("ortho", "Raster", true, 1, externalSource,
            "{\"Tipo\":\"Arquivo\"}", null),
        new CamadaProjetoSnapshot("OpenStreetMap", "Raster", true, 2, string.Empty,
            "{\"Tipo\":\"MapaBaseOnline\",\"Identificador\":\"OSM\"}", null),
        new CamadaProjetoSnapshot("public.parcels · geom", "Vetor", true, 3, string.Empty, postgisJson, "{\"color\":\"#087\"}")
    };
    const string layout = "{\"format\":\"geonex-layout\",\"version\":1,\"items\":[]}";
    await GnxProjectStore.SalvarAsync(project, rows, "EPSG:31982", "roads", 125.5, -42.25, true,
        16.75, -90.5, 2.25, layout);

    Projeto reopened = await GnxProjectStore.AbrirAsync(projectPath);
    Assert(reopened.CRS == "EPSG:31982", "CRS round trip");
    Assert(reopened.CamadaBase == "roads", "base layer round trip");
    Assert(reopened.OffsetMundoDefinido && reopened.OffsetMundoX == 125.5 && reopened.OffsetMundoY == -42.25, "origin round trip");
    Assert(reopened.CameraPanX == 16.75 && reopened.CameraPanY == -90.5 && reopened.CameraZoom == 2.25, "camera round trip");
    Assert(reopened.LayoutJson == layout, "print layout round trip");
    Assert(reopened.Camadas.Count == 4, "layer count round trip");
    var roads = reopened.Camadas.Single(layer => layer.Nome == "roads");
    Assert(!roads.Visivel && roads.Ordem == 0, "visibility and order round trip");
    Assert(roads.CaminhoFonteOriginal == nestedSource, "relative project path resolves against project folder");
    var reopenedRoadStyle = System.Text.Json.JsonSerializer.Deserialize<EstiloCamada>(roads.EstiloJson!)!;
    Assert(reopenedRoadStyle.TipoSimbologia == "CATEGORIZADA" &&
        reopenedRoadStyle.ColunaSimbologia == "zone" &&
        reopenedRoadStyle.CoresCategorizadas["industrial"] == "#b50",
        "categorized symbology round trip");
    Assert(reopenedRoadStyle.ExibirRotulos && reopenedRoadStyle.ColunaRotulo == "street_name" &&
        reopenedRoadStyle.CorTextoRotulo == "#ffffff" && reopenedRoadStyle.TamanhoTextoRotulo == 14 &&
        reopenedRoadStyle.TamanhoHaloRotulo == 4 && !reopenedRoadStyle.AntiColisaoRotulos,
        "label settings round trip");
    Assert(reopened.Camadas.Single(layer => layer.Nome == "ortho").CaminhoFonteOriginal == externalSource,
        "external absolute source remains absolute");
    Assert(reopened.Camadas.Single(layer => layer.Nome == "OpenStreetMap").FonteJson!.Contains("OSM", StringComparison.Ordinal),
        "online source descriptor round trip");
    var reopenedPostgisSource = System.Text.Json.JsonSerializer.Deserialize<ProjetoFonteCamada>(
        reopened.Camadas.Single(layer => layer.Nome == "public.parcels · geom").FonteJson!)!;
    Assert(reopenedPostgisSource.Servidor == "gis.example.test" && reopenedPostgisSource.Tabela == "parcels",
        "PostGIS connection descriptor round trip");
    Assert(!postgisJson.Contains("Password", StringComparison.OrdinalIgnoreCase), "PostGIS descriptor has no secret property");
    string secretKey = GnxProjectStore.ChaveSeguraPostgis(project.Id, "public.parcels · geom");
    Assert(secretKey.StartsWith($"GeoNex.PostGIS.{project.Id:N}.", StringComparison.Ordinal) &&
        !secretKey.Contains("parcels", StringComparison.OrdinalIgnoreCase), "PostGIS secret key is project-scoped and opaque");

    await using var context = new GeoNexContext(projectPath);
    string storedRoadPath = (await context.Camadas.AsNoTracking().SingleAsync(layer => layer.Nome == "roads")).CaminhoFonteOriginal!;
    Assert(storedRoadPath == Path.Combine("data", "roads.shp"), "project-contained sources are stored relatively");
    string storedSecretSource = (await context.Camadas.AsNoTracking().SingleAsync(layer => layer.Nome == "OpenStreetMap")).FonteJson!;
    Assert(storedSecretSource.Contains("OSM", StringComparison.Ordinal), "online source metadata round trip");
    await AssertThrowsAsync<InvalidDataException>(
        () => GnxProjectStore.SalvarLayoutAsync(reopened, new string('x', 20_000_001)),
        "reject overlarge print layout");
}

static async Task VerifyCreateDoesNotOverwriteExistingAsync(string root)
{
    string path = Path.Combine(root, "already-exists.gnx");
    const string sentinel = "keep this existing file";
    await File.WriteAllTextAsync(path, sentinel);
    await AssertThrowsAsync<IOException>(
        () => GnxProjectStore.CriarAsync(path, "Must not overwrite"),
        "refuse to replace an existing project file");
    Assert(await File.ReadAllTextAsync(path) == sentinel, "create collision preserves existing file bytes");
}

static async Task VerifyExtensionValidationAsync(string root)
{
    string path = Path.Combine(root, "wrong-extension.sqlite");
    await AssertThrowsAsync<InvalidDataException>(
        () => GnxProjectStore.CriarAsync(path, "Wrong extension"),
        "require .gnx extension when creating a project");
    Assert(!File.Exists(path), "invalid project extension does not create a file");
}

static async Task VerifySaveRollbackAsync(string root)
{
    string path = Path.Combine(root, "transaction.gnx");
    Projeto project = await GnxProjectStore.CriarAsync(path, "Transactional project");
    var originalLayer = new CamadaProjetoSnapshot("kept", "Vetor", true, 0, string.Empty,
        "{\"Tipo\":\"Arquivo\"}", "style-before");
    await GnxProjectStore.SalvarAsync(project, [originalLayer], "EPSG:4326", null, 0, 0, false, 0, 0, 1, null);

    await using (var context = new GeoNexContext(path))
        await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_project_layer BEFORE INSERT ON Camadas BEGIN SELECT RAISE(ABORT, 'intentional test failure'); END;");

    var rejectedLayer = new CamadaProjetoSnapshot("reject", "Vetor", true, 0, string.Empty,
        "{\"Tipo\":\"Arquivo\"}", null);
    await AssertThrowsAsync<DbUpdateException>(
        () => GnxProjectStore.SalvarAsync(project, [rejectedLayer], "EPSG:3857", "reject", 9, 8, true,
            7, 6, 5, "new layout", criarPontoRestauracao: true),
        "rollback project save when a layer write fails");

    Projeto reopened = await GnxProjectStore.AbrirAsync(path);
    Assert(reopened.CRS == "EPSG:4326" && reopened.Camadas.Single().Nome == "kept",
        "failed save preserves the previous project state");
    Assert(reopened.Camadas.Single().EstiloJson == "style-before" && reopened.LayoutJson is null,
        "failed save rolls back layer styles and layout data");
    Projeto recovery = await GnxProjectStore.AbrirAsync(GnxProjectStore.ObterCaminhoPontoRestauracao(path));
    Assert(recovery.CRS == "EPSG:4326" && recovery.Camadas.Single().Nome == "kept" &&
        recovery.Camadas.Single().EstiloJson == "style-before",
        "recovery point remains usable if the new project transaction fails");
}

static async Task VerifyDuplicateSaveRejectedWithoutMutationAsync(string root)
{
    string path = Path.Combine(root, "duplicate-save.gnx");
    Projeto project = await GnxProjectStore.CriarAsync(path, "Duplicate save project");
    var original = new CamadaProjetoSnapshot("Roads", "Vetor", true, 0, "roads.shp",
        "{\"Tipo\":\"Arquivo\"}", "original-style");
    await GnxProjectStore.SalvarAsync(project, [original], "EPSG:4326", null, 0, 0, false, 0, 0, 1, null);

    var updated = new CamadaProjetoSnapshot("ROADS", "Raster", false, 0, "roads.tif",
        "{\"Tipo\":\"Arquivo\"}", "replacement-style");
    await AssertThrowsAsync<InvalidDataException>(
        () => GnxProjectStore.SalvarAsync(project, [original, updated], "EPSG:3857", "ROADS", 10, 20, true, 7, 8, 2, "new-layout"),
        "reject duplicate layer names before save");

    Projeto reopened = await GnxProjectStore.AbrirAsync(path);
    Assert(reopened.CRS == "EPSG:4326" && reopened.CamadaBase is null && reopened.LayoutJson is null,
        "duplicate layer rejection leaves project-level state intact");
    Assert(reopened.Camadas.Single().Nome == "Roads" && reopened.Camadas.Single().EstiloJson == "original-style",
        "duplicate layer rejection does not discard or replace an existing layer");
}

static async Task VerifyLegacyMigrationAsync(string root)
{
    string path = Path.Combine(root, "legacy.gnx");
    Guid id = Guid.NewGuid();
    await using (var connection = new SqliteConnection($"Data Source={path}"))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Projetos (Id TEXT PRIMARY KEY, Nome TEXT NOT NULL, CaminhoArquivo TEXT NOT NULL, CRS TEXT NOT NULL, CriadoEm TEXT NOT NULL);
            CREATE TABLE Camadas (Id INTEGER PRIMARY KEY AUTOINCREMENT, Nome TEXT NOT NULL, Tipo TEXT NOT NULL, Opacidade REAL NOT NULL, Visivel INTEGER NOT NULL, Ordem INTEGER NOT NULL, CaminhoFonteOriginal TEXT NULL, ProjetoId TEXT NOT NULL);
            INSERT INTO Projetos (Id, Nome, CaminhoArquivo, CRS, CriadoEm) VALUES ($id, 'Old project', $path, 'EPSG:4326', '2020-01-01 00:00:00');
            INSERT INTO Camadas (Nome, Tipo, Opacidade, Visivel, Ordem, CaminhoFonteOriginal, ProjetoId) VALUES ('legacy', 'Vetor', 1, 1, 0, 'legacy.shp', $id);
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync();
    }

    Projeto project = await GnxProjectStore.AbrirAsync(path);
    Assert(project.Id == id && project.Camadas.Single().Nome == "legacy", "legacy project data survives migration");
    Assert(project.Camadas.Single().CaminhoFonteOriginal == Path.GetFullPath(Path.Combine(root, "legacy.shp")),
        "legacy relative source path resolves from the project directory");
    await GnxProjectStore.AbrirAsync(path);
    await using var migrated = new GeoNexContext(path);
    var columns = await migrated.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Projetos')").ToListAsync();
    Assert(columns.Contains("LayoutJson") && columns.Contains("CameraZoom"), "legacy project receives current columns");
    var version = await migrated.Database.SqlQueryRaw<int>("PRAGMA user_version").ToListAsync();
    Assert(version.Single() == GnxProjectStore.CurrentSchemaVersion, "schema migration is versioned and idempotent");
}

static async Task VerifyInvalidAndFutureProjectsAsync(string root)
{
    string invalid = Path.Combine(root, "not-a-project.gnx");
    await using (var connection = new SqliteConnection($"Data Source={invalid}"))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unrelated (id INTEGER PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
    }
    await AssertThrowsAsync<InvalidDataException>(() => GnxProjectStore.AbrirAsync(invalid), "reject arbitrary SQLite database");

    string future = Path.Combine(root, "future.gnx");
    await using (var connection = new SqliteConnection($"Data Source={future}"))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Projetos (Id TEXT PRIMARY KEY, Nome TEXT NOT NULL, CaminhoArquivo TEXT NOT NULL, CRS TEXT NOT NULL, CriadoEm TEXT NOT NULL);
            CREATE TABLE Camadas (Id INTEGER PRIMARY KEY, Nome TEXT NOT NULL, Tipo TEXT NOT NULL, Opacidade REAL NOT NULL, Visivel INTEGER NOT NULL, Ordem INTEGER NOT NULL, CaminhoFonteOriginal TEXT NULL, ProjetoId TEXT NOT NULL);
            PRAGMA user_version = 999;
            """;
        await command.ExecuteNonQueryAsync();
    }
    await AssertThrowsAsync<InvalidDataException>(() => GnxProjectStore.AbrirAsync(future), "reject newer schema without mutation");
    await using var futureConnection = new SqliteConnection($"Data Source={future}");
    await futureConnection.OpenAsync();
    await using var versionCommand = futureConnection.CreateCommand();
    versionCommand.CommandText = "PRAGMA user_version";
    Assert(Convert.ToInt32(await versionCommand.ExecuteScalarAsync()) == 999, "future schema version remains untouched");
}

static async Task VerifyDuplicateLayerValidationAsync(string root)
{
    string path = Path.Combine(root, "duplicate-layers.gnx");
    Projeto project = await GnxProjectStore.CriarAsync(path, "Duplicate layer names");
    var first = new CamadaProjetoSnapshot("Roads", "Vetor", true, 0, string.Empty,
        "{\"Tipo\":\"Arquivo\"}", null);
    await GnxProjectStore.SalvarAsync(project, [first], "EPSG:4326", null, 0, 0, false, 0, 0, 1, null);
    await using (var context = new GeoNexContext(path))
    {
        context.Camadas.Add(new Camada { Nome = "roads", Tipo = "Vetor", ProjetoId = project.Id, Ordem = 1 });
        await context.SaveChangesAsync();
    }

    await AssertThrowsAsync<InvalidDataException>(
        () => GnxProjectStore.AbrirAsync(path), "reject duplicate layer names case-insensitively");
}

static async Task VerifyDistinctLayerNamesWithSharedSourceBasenameAsync(string root)
{
    string path = Path.Combine(root, "shared-source-basename.gnx");
    Projeto project = await GnxProjectStore.CriarAsync(path, "Shared source basename");
    var layers = new[]
    {
        new CamadaProjetoSnapshot("north-roads", "Vetor", true, 0, Path.Combine(root, "north", "roads.shp"),
            "{\"Tipo\":\"Arquivo\"}", null),
        new CamadaProjetoSnapshot("south-roads", "Vetor", true, 1, Path.Combine(root, "south", "roads.shp"),
            "{\"Tipo\":\"Arquivo\"}", null)
    };
    await GnxProjectStore.SalvarAsync(project, layers, "EPSG:4326", null, 0, 0, false, 0, 0, 1, null);
    Projeto reopened = await GnxProjectStore.AbrirAsync(path);
    Assert(reopened.Camadas.Count == 2 &&
        reopened.Camadas.Any(layer => layer.Nome == "north-roads") &&
        reopened.Camadas.Any(layer => layer.Nome == "south-roads"),
        "preserve project layer names when source files share a basename");
}

static void VerifyPathResolution(string root)
{
    string projectPath = Path.Combine(root, "project", "nested", "map.gnx");
    string inProject = Path.Combine(root, "project", "nested", "layers", "buildings.shp");
    string outside = Path.Combine(root, "external", "imagery.tif");
    string storedRelative = GnxProjectStore.PersistirCaminhoFonte(projectPath, inProject);
    Assert(!Path.IsPathRooted(storedRelative), "contained source path stored relatively");
    Assert(GnxProjectStore.ResolverCaminhoFonte(projectPath, storedRelative) == Path.GetFullPath(inProject), "relative path resolves after reopen");
    Assert(GnxProjectStore.PersistirCaminhoFonte(projectPath, outside) == Path.GetFullPath(outside), "external source stays absolute");
    Assert(GnxProjectStore.ResolverCaminhoFonte(projectPath, "https://example.test/tiles/{z}/{x}/{y}.png").StartsWith("https://", StringComparison.Ordinal), "remote URI preserved");
}

static async Task VerifyMovedProjectResolvesRelativeSourcesAsync(string root)
{
    string originalDirectory = Path.Combine(root, "relocation", "original");
    string originalProjectPath = Path.Combine(originalDirectory, "portable.gnx");
    string originalSourcePath = Path.Combine(originalDirectory, "layers", "trees.geojson");
    Directory.CreateDirectory(Path.GetDirectoryName(originalSourcePath)!);
    await File.WriteAllTextAsync(originalSourcePath, "relative source fixture");

    Projeto project = await GnxProjectStore.CriarAsync(originalProjectPath, "Portable project");
    var layer = new CamadaProjetoSnapshot("trees.geojson", "Vetor", true, 0, originalSourcePath,
        "{\"Tipo\":\"Arquivo\"}", null);
    await GnxProjectStore.SalvarAsync(project, [layer], "EPSG:4326", "trees.geojson",
        0, 0, true, 0, 0, 1, null);

    SqliteConnection.ClearAllPools();
    string movedDirectory = Path.Combine(root, "relocation", "moved");
    Directory.Move(originalDirectory, movedDirectory);
    string movedProjectPath = Path.Combine(movedDirectory, "portable.gnx");
    string movedSourcePath = Path.Combine(movedDirectory, "layers", "trees.geojson");
    Projeto reopened = await GnxProjectStore.AbrirAsync(movedProjectPath);
    Assert(reopened.CaminhoArquivo == Path.GetFullPath(movedProjectPath),
        "moved project uses its new document path");
    Assert(reopened.Camadas.Single().CaminhoFonteOriginal == Path.GetFullPath(movedSourcePath) &&
        File.Exists(reopened.Camadas.Single().CaminhoFonteOriginal),
        "relative source resolves after moving the project folder");
}

static async Task VerifyProjectSourceValidationAsync(string root)
{
    ConfigureGdalNativeRuntime();
    object gdalLock = new();
    string missingPath = Path.Combine(root, "missing.shp");
    FileNotFoundException missing = await AssertThrowsAsync<FileNotFoundException>(
        () => GnxProjectSourceValidator.ValidarAsync(
            [new Camada { Nome = "missing", Tipo = "Vetor", CaminhoFonteOriginal = missingPath }], gdalLock),
        "reject missing local sources before map replacement");
    Assert(missing.Message.Contains("missing", StringComparison.OrdinalIgnoreCase) &&
        missing.Message.Contains(missingPath, StringComparison.OrdinalIgnoreCase) &&
        missing.Message.Contains("Ação sugerida", StringComparison.OrdinalIgnoreCase),
        "explain which missing source failed and how to repair it");

    string invalidRasterPath = Path.Combine(root, "invalid.tif");
    await File.WriteAllTextAsync(invalidRasterPath, "not a raster dataset");
    InvalidDataException invalidRaster = await AssertThrowsAsync<InvalidDataException>(
        () => GnxProjectSourceValidator.ValidarAsync(
            [new Camada { Nome = "invalid raster", Tipo = "Raster", CaminhoFonteOriginal = invalidRasterPath }], gdalLock),
        "reject existing but unreadable raster sources");
    Assert(invalidRaster.Message.Contains("invalid raster", StringComparison.OrdinalIgnoreCase) &&
        invalidRaster.Message.Contains(invalidRasterPath, StringComparison.OrdinalIgnoreCase) &&
        invalidRaster.Message.Contains("Ação sugerida", StringComparison.OrdinalIgnoreCase),
        "include layer, source path, and a recovery action for invalid rasters");

    string invalidVectorPath = Path.Combine(root, "invalid.shp");
    await File.WriteAllTextAsync(invalidVectorPath, "not a shapefile");
    InvalidDataException invalidVector = await AssertThrowsAsync<InvalidDataException>(
        () => GnxProjectSourceValidator.ValidarAsync(
            [new Camada { Nome = "invalid vector", Tipo = "Vetor", CaminhoFonteOriginal = invalidVectorPath }], gdalLock),
        "reject existing but unreadable vector sources");
    Assert(invalidVector.Message.Contains("invalid vector", StringComparison.OrdinalIgnoreCase) &&
        invalidVector.Message.Contains(invalidVectorPath, StringComparison.OrdinalIgnoreCase) &&
        invalidVector.Message.Contains(".shx", StringComparison.OrdinalIgnoreCase),
        "explain required Shapefile companion files after a vector source failure");

    IReadOnlyList<string> relatorio = await GnxProjectSourceValidator.ValidarTodasAsync(
    [
        new Camada { Nome = "invalid raster", Tipo = "Raster", CaminhoFonteOriginal = invalidRasterPath },
        new Camada { Nome = "invalid vector", Tipo = "Vetor", CaminhoFonteOriginal = invalidVectorPath }
    ], gdalLock);
    Assert(relatorio.Count == 2 &&
        relatorio.Any(item => item.Contains("invalid raster", StringComparison.OrdinalIgnoreCase)) &&
        relatorio.Any(item => item.Contains("invalid vector", StringComparison.OrdinalIgnoreCase)),
        "collect errors for every invalid layer before a project replaces the map");

    string validVectorPath = Path.Combine(root, "valid.geojson");
    await File.WriteAllTextAsync(validVectorPath,
        "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"id\":1},\"geometry\":{\"type\":\"Point\",\"coordinates\":[10,20]}}]}");
    await GnxProjectSourceValidator.ValidarAsync(
        [new Camada { Nome = "valid vector", Tipo = "Vetor", CaminhoFonteOriginal = validVectorPath }], gdalLock);

    await AssertThrowsAsync<InvalidDataException>(
        () => GnxProjectSourceValidator.ValidarAsync(
            [new Camada { Nome = "temporary", Tipo = "Raster", CaminhoFonteOriginal = "/vsimem/temporary.tif" }], gdalLock),
        "reject process-local GDAL virtual paths");
}

static void ConfigureGdalNativeRuntime()
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("O runtime GDAL deste harness é empacotado para Windows.");

    string runtimeRoot = Path.Combine(AppContext.BaseDirectory, "gdal");
    string nativeDirectory = Path.Combine(runtimeRoot, Environment.Is64BitProcess ? "x64" : "x86");
    if (!Directory.Exists(nativeDirectory) || !GdalTestRuntime.SetDllDirectory(nativeDirectory))
        throw new InvalidOperationException($"Não foi possível configurar o runtime GDAL em {nativeDirectory}.");

    string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    Environment.SetEnvironmentVariable("PATH", nativeDirectory + Path.PathSeparator + currentPath);
    Environment.SetEnvironmentVariable("GDAL_DATA", Path.Combine(runtimeRoot, "data"));
    Environment.SetEnvironmentVariable("PROJ_DATA", Path.Combine(runtimeRoot, "share"));
    Environment.SetEnvironmentVariable("PROJ_LIB", Path.Combine(runtimeRoot, "share"));
}

static void Assert(bool condition, string contract)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {contract}");
}

static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action, string contract) where TException : Exception
{
    try { await action(); }
    catch (TException exception) { return exception; }
    throw new InvalidOperationException($"FAIL: {contract} did not throw {typeof(TException).Name}");
}

static class GdalTestRuntime
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDllDirectory(string path);
}
