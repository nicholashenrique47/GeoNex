using System.Text;

namespace GeoNex.Services;

/// <summary>
/// Stages vector exports beside the destination and publishes them only after validation.
/// A small journal allows an interrupted multi-file Shapefile publication to be rolled back.
/// </summary>
internal sealed class ExportPublicationTransaction : IDisposable
{
    private const string BackupCompleteMarker = "backup-complete";
    private const string PublishedMarker = "published";
    private const string ManifestFileName = "manifest.txt";

    private static readonly string[] ShapefileSuffixes =
    [
        ".shp", ".shx", ".dbf", ".prj", ".cpg", ".qix", ".sbn", ".sbx",
        ".shp.xml", ".aih", ".ain", ".ixs", ".mxs", ".atx"
    ];

    private readonly string _destinationPath;
    private readonly string _destinationDirectory;
    private readonly string _transactionRoot;
    private readonly string _stagingDirectory;
    private readonly string _backupDirectory;
    private readonly bool _isShapefile;
    private readonly FileStream _lockStream;
    private bool _published;
    private bool _disposed;

    private ExportPublicationTransaction(
        string destinationPath,
        bool isShapefile,
        string transactionRoot,
        FileStream lockStream)
    {
        _destinationPath = destinationPath;
        _destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("O destino de exportação não possui diretório.");
        _isShapefile = isShapefile;
        _transactionRoot = transactionRoot;
        _stagingDirectory = Path.Combine(transactionRoot, "staging");
        _backupDirectory = Path.Combine(transactionRoot, "backup");
        _lockStream = lockStream;

        Directory.CreateDirectory(_stagingDirectory);
        Directory.CreateDirectory(_backupDirectory);
        StagingPath = Path.Combine(_stagingDirectory, Path.GetFileName(destinationPath));
    }

    public string StagingPath { get; }

    public static ExportPublicationTransaction Begin(string destinationPath, bool isShapefile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string fullDestination = Path.GetFullPath(destinationPath);
        string? destinationDirectory = Path.GetDirectoryName(fullDestination);
        string destinationFileName = Path.GetFileName(fullDestination);
        if (string.IsNullOrWhiteSpace(destinationDirectory) || string.IsNullOrWhiteSpace(destinationFileName))
            throw new ArgumentException("O caminho de destino deve apontar para um arquivo.", nameof(destinationPath));

        Directory.CreateDirectory(destinationDirectory);
        string prefix = GetTransactionPrefix(destinationFileName);
        string lockPath = Path.Combine(destinationDirectory, prefix + ".lock");
        FileStream? lockStream = null;

        try
        {
            lockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            RecoverInterruptedTransactions(fullDestination);

            string transactionRoot = Path.Combine(
                destinationDirectory,
                $"{prefix}-{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(transactionRoot);
            return new ExportPublicationTransaction(fullDestination, isShapefile, transactionRoot, lockStream);
        }
        catch
        {
            lockStream?.Dispose();
            throw;
        }
    }

    public void Publish(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_published) throw new InvalidOperationException("A exportação já foi publicada.");
        cancellationToken.ThrowIfCancellationRequested();

        string[] outputNames = BuildAndValidateManifest();
        WriteManifest(outputNames);

        try
        {
            if (_isShapefile)
                PublishShapefile(outputNames, cancellationToken);
            else
                PublishSingleFile(outputNames[0], cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            WriteDurableMarker(Path.Combine(_transactionRoot, PublishedMarker));
            _published = true;
            TryDeleteDirectory(_transactionRoot);
        }
        catch (Exception publicationError)
        {
            try
            {
                RollbackTransaction(_transactionRoot, _destinationPath);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "A publicação falhou e o rollback automático não pôde ser concluído. " +
                    $"Os dados de recuperação foram preservados em '{_transactionRoot}'.",
                    publicationError,
                    rollbackError);
            }

            throw new IOException("A publicação da exportação falhou; o destino anterior foi restaurado.", publicationError);
        }
    }

    private string[] BuildAndValidateManifest()
    {
        string[] stagedFiles = Directory.GetFiles(_stagingDirectory, "*", SearchOption.TopDirectoryOnly);
        string destinationName = Path.GetFileName(_destinationPath);

        if (!_isShapefile)
        {
            if (stagedFiles.Length != 1 ||
                !string.Equals(Path.GetFileName(stagedFiles[0]), destinationName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("O driver não produziu exatamente o arquivo de destino esperado.");
            }

            return [destinationName];
        }

        string destinationStem = Path.GetFileNameWithoutExtension(_destinationPath);
        var outputNames = new List<string>(stagedFiles.Length);
        foreach (string stagedFile in stagedFiles)
        {
            string name = Path.GetFileName(stagedFile);
            if (!TryGetShapefileSuffix(name, destinationStem, out _))
                throw new InvalidDataException($"Sidecar inesperado produzido pelo driver: '{name}'.");
            outputNames.Add(name);
        }

        foreach (string requiredSuffix in new[] { ".shp", ".shx", ".dbf" })
        {
            if (!outputNames.Any(name =>
                    TryGetShapefileSuffix(name, destinationStem, out string suffix) &&
                    string.Equals(suffix, requiredSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"O Shapefile temporário não contém o componente obrigatório '{requiredSuffix}'.");
            }
        }

        return outputNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void PublishSingleFile(string outputName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string stagedPath = Path.Combine(_stagingDirectory, outputName);
        string destinationPath = Path.Combine(_destinationDirectory, outputName);
        string backupPath = Path.Combine(_backupDirectory, outputName);

        if (File.Exists(destinationPath))
        {
            File.Replace(stagedPath, destinationPath, backupPath, ignoreMetadataErrors: true);
            WriteDurableMarker(Path.Combine(_transactionRoot, BackupCompleteMarker));
            return;
        }

        WriteDurableMarker(Path.Combine(_transactionRoot, BackupCompleteMarker));
        File.Move(stagedPath, destinationPath);
    }

    private void PublishShapefile(IReadOnlyCollection<string> outputNames, CancellationToken cancellationToken)
    {
        string destinationStem = Path.GetFileNameWithoutExtension(_destinationPath);
        foreach (string existingPath in Directory.GetFiles(_destinationDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => TryGetShapefileSuffix(Path.GetFileName(path), destinationStem, out _)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(existingPath, Path.Combine(_backupDirectory, Path.GetFileName(existingPath)));
        }

        WriteDurableMarker(Path.Combine(_transactionRoot, BackupCompleteMarker));

        foreach (string outputName in outputNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(
                Path.Combine(_stagingDirectory, outputName),
                Path.Combine(_destinationDirectory, outputName));
        }
    }

    private void WriteManifest(IEnumerable<string> outputNames)
    {
        string manifestPath = Path.Combine(_transactionRoot, ManifestFileName);
        using var stream = new FileStream(
            manifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        foreach (string outputName in outputNames)
            writer.WriteLine(outputName);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void RecoverInterruptedTransactions(string destinationPath)
    {
        string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        string prefix = GetTransactionPrefix(Path.GetFileName(destinationPath));
        foreach (string transactionRoot in Directory.GetDirectories(
                     destinationDirectory,
                     prefix + "-*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            if (File.Exists(Path.Combine(transactionRoot, PublishedMarker)))
            {
                TryDeleteDirectory(transactionRoot);
                continue;
            }

            RollbackTransaction(transactionRoot, destinationPath);
        }
    }

    private static void RollbackTransaction(string transactionRoot, string destinationPath)
    {
        if (!Directory.Exists(transactionRoot)) return;

        string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        string backupDirectory = Path.Combine(transactionRoot, "backup");
        string manifestPath = Path.Combine(transactionRoot, ManifestFileName);
        string[] outputNames = File.Exists(manifestPath)
            ? File.ReadAllLines(manifestPath, Encoding.UTF8)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(ValidateManifestName)
                .ToArray()
            : [];
        bool publicationStarted = File.Exists(Path.Combine(transactionRoot, BackupCompleteMarker));

        if (publicationStarted)
        {
            foreach (string outputName in outputNames)
            {
                string publishedPath = Path.Combine(destinationDirectory, outputName);
                if (File.Exists(publishedPath)) File.Delete(publishedPath);
            }
        }

        if (Directory.Exists(backupDirectory))
        {
            foreach (string backupPath in Directory.GetFiles(backupDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                string backupName = ValidateManifestName(Path.GetFileName(backupPath));
                string restorePath = Path.Combine(destinationDirectory, backupName);

                // File.Replace creates the backup atomically before the marker is written.
                if (File.Exists(restorePath) && outputNames.Contains(backupName, StringComparer.OrdinalIgnoreCase))
                    File.Delete(restorePath);
                else if (File.Exists(restorePath))
                    throw new IOException($"Não foi possível restaurar '{backupName}' sem sobrescrever outro arquivo.");

                File.Move(backupPath, restorePath);
            }
        }

        TryDeleteDirectory(transactionRoot);
    }

    private static string ValidateManifestName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
            throw new InvalidDataException("Manifesto de exportação contém caminho inválido.");
        return name;
    }

    private static bool TryGetShapefileSuffix(string fileName, string stem, out string suffix)
    {
        suffix = string.Empty;
        if (!fileName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return false;

        string candidate = fileName[stem.Length..];
        if (!ShapefileSuffixes.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return false;
        suffix = candidate;
        return true;
    }

    private static string GetTransactionPrefix(string destinationFileName) =>
        $".{destinationFileName}.geonex-export";

    private static void WriteDurableMarker(string markerPath)
    {
        using var stream = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.WriteByte(1);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A committed journal can be cleaned during the next export.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the journal when antivirus/indexing temporarily holds a file.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_published && Directory.Exists(_transactionRoot))
        {
            try
            {
                RollbackTransaction(_transactionRoot, _destinationPath);
            }
            catch
            {
                // Keep the journal for recovery on the next attempt.
            }
        }

        _lockStream.Dispose();
    }
}
