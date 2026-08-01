using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibFileParameters.Models;
using ToolsManagement.FileManagersMain;
using UsbCopyService.Hubs;
using UsbCopyService.Settings;
using UsbCopyServiceShared.Contracts;

namespace UsbCopyService.Jobs;

//ერთი კლიენტის მოთხოვნით გაშვებული სამუშაო: წყაროდან ფაილების ჩამოტვირთვა, ოპტიმიზაცია და კლიენტისთვის მიწოდება
public sealed class CopyJob : IDisposable
{
    private const string DownloadTempExtension = "dwn";
    private const int CopyBufferSize = 81920;

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ExcludeSet? _excludeSet;
    private readonly HashSet<string> _existingFiles;
    private readonly FileStorageData _fileStorageData;
    private readonly IHubContext<UsbCopyHub> _hubContext;
    private readonly ILogger _logger;
    private readonly string _outDir;
    private readonly string _projectName;
    private readonly UsbCopySettings _settings;
    private readonly string _srcDir;
    private readonly string _workDir;

    private volatile PackageCurrent? _current;

    // ReSharper disable once ConvertToPrimaryConstructor
    public CopyJob(ILogger logger, IHubContext<UsbCopyHub> hubContext, UsbCopySettings settings, string connectionId,
        string projectName, FileStorageData fileStorageData, ExcludeSet? excludeSet, string[] existingFiles)
    {
        _logger = logger;
        _hubContext = hubContext;
        _settings = settings;
        ConnectionId = connectionId;
        _projectName = projectName;
        _fileStorageData = fileStorageData;
        _excludeSet = excludeSet;
        _existingFiles = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);

        JobId = Guid.NewGuid().ToString("N");
        _workDir = Path.Combine(settings.WorkPath ?? string.Empty, "job_" + JobId);
        _srcDir = Path.Combine(_workDir, "src");
        _outDir = Path.Combine(_workDir, "out");
    }

    public string JobId { get; }

    public string ConnectionId { get; }

    public void Dispose()
    {
        _cancellationTokenSource.Dispose();
    }

    //სამუშაოს ფონურად გაშვება; დასრულებისას onFinished ეძახება რეესტრიდან ამოსაშლელად
    public void Start(Action<CopyJob> onFinished)
    {
        _ = RunAndCleanup(onFinished);
    }

    private async Task RunAndCleanup(Action<CopyJob> onFinished)
    {
        try
        {
            await RunAsync();
        }
        finally
        {
            onFinished(this);
            Dispose();
        }
    }

    public void Cancel()
    {
        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            //სამუშაო უკვე დასრულებულია
        }
    }

    //კლიენტის დასტური მიმდინარე პაკეტზე
    public void SetAck(string packageId, bool ok, string? errorMessage)
    {
        PackageCurrent? current = _current;
        if (current is null || !string.Equals(current.Source.PackageId, packageId, StringComparison.Ordinal))
        {
            _logger.LogWarning("Ack for unknown package {PackageId} in job {JobId} ignored", packageId, JobId);
            return;
        }

        current.AckTcs.TrySetResult(new AckResult(ok, errorMessage));
    }

    //download ენდპოინტისთვის: მხოლოდ მიმდინარე შეთავაზებული პაკეტის წყარო ბრუნდება
    public PackageSource? GetCurrentPackage(string packageId)
    {
        PackageCurrent? current = _current;
        if (current is null || !string.Equals(current.Source.PackageId, packageId, StringComparison.Ordinal))
        {
            return null;
        }

        return current.Source;
    }

    private async Task RunAsync()
    {
        CancellationToken cancellationToken = _cancellationTokenSource.Token;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await SendProgress($"Job {JobId} for project {_projectName} started", cancellationToken);

            (FileManager fileManager, WalkResult walkResult) = await CollectFiles(cancellationToken);

            var summary = new JobSummary
            {
                FilesTotal = walkResult.Files.Count,
                FilesSkipped = walkResult.SkippedExisting,
                BytesOriginal = walkResult.Files.Sum(f => f.FileLength)
            };

            await SendProgress(
                $"Selected {summary.FilesTotal} files ({summary.BytesOriginal} bytes), skipped {summary.FilesSkipped} existing",
                cancellationToken);

            if (walkResult.Files.Count > 0)
            {
                CheckFreeSpace(walkResult.Files);

                await DownloadAll(fileManager, walkResult.Files, cancellationToken);

                List<PlannedPackage> plan = PackagePlanner.Plan(walkResult.Files, _settings.SmallFileMaxSizeBytes,
                    _settings.ArchiveVolumeMaxSizeBytes, _settings.PartMaxSizeBytes);

                summary.PackagesTotal = plan.Count;
                await SendProgress($"Prepared {plan.Count} packages", cancellationToken);

                summary.BytesTransferred = await ServePackages(plan, cancellationToken);
            }

            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            summary.ElapsedSeconds = elapsedSeconds;
            await _hubContext.Clients.Client(ConnectionId).SendAsync(UsbCopyHubEvents.ReceiveJobCompleted,
                JsonSerializer.Serialize(summary), cancellationToken);
            _logger.LogInformation("Job {JobId} completed in {Elapsed} seconds", JobId, elapsedSeconds);
        }
        catch (OperationCanceledException e)
        {
            _logger.LogInformation(e, "Job {JobId} was canceled", JobId);
            await TrySendFailed("Job was canceled");
        }
        catch (UsbCopyJobException e)
        {
            _logger.LogError(e, "Job {JobId} failed", JobId);
            await TrySendFailed(e.Message);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Job {JobId} failed unexpectedly", JobId);
            await TrySendFailed(e.Message);
        }
        finally
        {
            CleanupWorkDir();
        }
    }

    private async Task<(FileManager FileManager, WalkResult WalkResult)> CollectFiles(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_outDir);

        FileManager fileManager = FileManagersFactoryExt.CreateFileManager(false, _logger, _srcDir, _fileStorageData) ??
                                  throw new UsbCopyJobException("fileManager does not created");

        string[] excludes = [];
        if (_excludeSet?.FolderFileMasks is { Count: > 0 })
        {
            excludes =
            [
                .. _excludeSet.FolderFileMasks.Select(s =>
                    s.Replace(Path.DirectorySeparatorChar, fileManager.DirectorySeparatorChar))
            ];
        }

        var walker = new RemoteTreeWalker(fileManager, excludes, _existingFiles,
            message => SendProgress(message, cancellationToken));
        WalkResult walkResult = await walker.CollectAsync(cancellationToken);
        return (fileManager, walkResult);
    }

    private void CheckFreeSpace(IReadOnlyCollection<RemoteFileEntry> files)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(_workDir));
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        long needed = files.Sum(f => f.FileLength) + _settings.ArchiveVolumeMaxSizeBytes;
        var driveInfo = new DriveInfo(root);
        if (driveInfo.AvailableFreeSpace < needed)
        {
            throw new UsbCopyJobException(
                $"Not enough free space on {root}: needed {needed} bytes, available {driveInfo.AvailableFreeSpace} bytes");
        }
    }

    private async Task DownloadAll(FileManager fileManager, IReadOnlyCollection<RemoteFileEntry> files,
        CancellationToken cancellationToken)
    {
        bool isDiskSource = fileManager is DiskFileManager;

        foreach (RemoteFileEntry entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string localFullPath = GetLocalFullPath(entry);
            string? localDir = Path.GetDirectoryName(localFullPath);
            if (!string.IsNullOrEmpty(localDir))
            {
                Directory.CreateDirectory(localDir);
            }

            await SendProgress($"Downloading {entry.WirePath}", cancellationToken);

            //DiskFileManager afterRootPath-ს არ ითვალისწინებს, ამიტომ მას სრული ფარდობითი გზა გადაეცემა
            bool downloaded = isDiskSource
                ? fileManager.DownloadFile(GetNativeRelativePath(fileManager, entry), DownloadTempExtension)
                : fileManager.DownloadFile(entry.FileName, DownloadTempExtension, entry.AfterRootPath);

            if (!downloaded)
            {
                throw new UsbCopyJobException($"Cannot download file {entry.WirePath}");
            }
        }
    }

    private static string GetNativeRelativePath(FileManager fileManager, RemoteFileEntry entry)
    {
        return entry.AfterRootPath is null
            ? entry.FileName
            : fileManager.PathCombine(entry.AfterRootPath, entry.FileName);
    }

    private string GetLocalFullPath(RemoteFileEntry entry)
    {
        return Path.Combine(_srcDir, entry.WirePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task<long> ServePackages(List<PlannedPackage> plan, CancellationToken cancellationToken)
    {
        long bytesTransferred = 0;

        for (var packageIndex = 0; packageIndex < plan.Count; packageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PlannedPackage plannedPackage = plan[packageIndex];
            (PackageSource source, PackageManifest manifest) =
                await PreparePackage(plannedPackage, packageIndex, plan.Count, cancellationToken);

            var ackTcs = new TaskCompletionSource<AckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _current = new PackageCurrent(source, ackTcs);

            await _hubContext.Clients.Client(ConnectionId).SendAsync(UsbCopyHubEvents.ReceivePackageReady,
                JsonSerializer.Serialize(manifest), cancellationToken);

            AckResult ackResult = await WaitAck(ackTcs, cancellationToken);
            _current = null;

            if (!ackResult.Ok)
            {
                throw new UsbCopyJobException(
                    $"Client rejected package {manifest.PackageId}: {ackResult.ErrorMessage ?? "unknown error"}");
            }

            bytesTransferred += source.Length;
            CleanupAfterAck(plannedPackage, source);

            await SendProgress($"Package {packageIndex + 1}/{plan.Count} delivered", cancellationToken);
        }

        return bytesTransferred;
    }

    private async Task<AckResult> WaitAck(TaskCompletionSource<AckResult> ackTcs, CancellationToken cancellationToken)
    {
        try
        {
            return await ackTcs.Task.WaitAsync(TimeSpan.FromMinutes(_settings.AckTimeoutMinutes), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new UsbCopyJobException(
                $"Client did not acknowledge package within {_settings.AckTimeoutMinutes} minutes");
        }
    }

    private async Task<(PackageSource Source, PackageManifest Manifest)> PreparePackage(PlannedPackage plannedPackage,
        int packageIndex, int packagesTotal, CancellationToken cancellationToken)
    {
        string packageId = Guid.NewGuid().ToString("N");

        var manifest = new PackageManifest
        {
            PackageId = packageId,
            JobId = JobId,
            PackageIndex = packageIndex,
            PackagesTotal = packagesTotal,
            PackageType = plannedPackage.PackageType
        };

        PackageSource source;
        switch (plannedPackage.PackageType)
        {
            case EPackageType.Archive:
                string zipPath = Path.Combine(_outDir,
                    "package_" + packageIndex.ToString("D4", CultureInfo.InvariantCulture) + ".zip");
                await CreateZip(plannedPackage.Files, zipPath, cancellationToken);
                long zipLength = new FileInfo(zipPath).Length;
                manifest.TransferSize = zipLength;
                manifest.FilesCount = plannedPackage.Files.Count;
                manifest.Sha256 = await ComputeHash(zipPath, 0, zipLength, cancellationToken);
                source = new PackageSource(packageId, zipPath, 0, zipLength);
                break;
            case EPackageType.WholeFile:
                RemoteFileEntry wholeFile = plannedPackage.Files[0];
                string wholeFilePath = GetLocalFullPath(wholeFile);
                manifest.RelativePath = wholeFile.WirePath;
                manifest.FileSize = wholeFile.FileLength;
                manifest.TransferSize = wholeFile.FileLength;
                manifest.Sha256 = await ComputeHash(wholeFilePath, 0, wholeFile.FileLength, cancellationToken);
                source = new PackageSource(packageId, wholeFilePath, 0, wholeFile.FileLength);
                break;
            case EPackageType.FilePart:
                RemoteFileEntry bigFile = plannedPackage.Files[0];
                string bigFilePath = GetLocalFullPath(bigFile);
                manifest.RelativePath = bigFile.WirePath;
                manifest.FileSize = bigFile.FileLength;
                manifest.PartIndex = plannedPackage.PartIndex;
                manifest.PartsTotal = plannedPackage.PartsTotal;
                manifest.PartOffset = plannedPackage.PartOffset;
                manifest.TransferSize = plannedPackage.PartLength;
                manifest.Sha256 = await ComputeHash(bigFilePath, plannedPackage.PartOffset, plannedPackage.PartLength,
                    cancellationToken);
                source = new PackageSource(packageId, bigFilePath, plannedPackage.PartOffset,
                    plannedPackage.PartLength);
                break;
            default:
                throw new UsbCopyJobException($"Unknown package type {plannedPackage.PackageType}");
        }

        return (source, manifest);
    }

    private async Task CreateZip(IReadOnlyCollection<RemoteFileEntry> files, string zipPath,
        CancellationToken cancellationToken)
    {
        // ReSharper disable once DisposableConstructor
        // ReSharper disable once using
        await using var zipFileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferSize, true);
        await using (zipFileStream.ConfigureAwait(false))
        {
            // ReSharper disable once using
            // ReSharper disable once DisposableConstructor
            await using var zipArchive = new ZipArchive(zipFileStream, ZipArchiveMode.Create);
            foreach (RemoteFileEntry entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ZipArchiveEntry zipEntry = zipArchive.CreateEntry(entry.WirePath, CompressionLevel.Optimal);
                // ReSharper disable once using
                await using Stream zipEntryStream = await zipEntry.OpenAsync(cancellationToken);
                await using (zipEntryStream.ConfigureAwait(false))
                {
                    // ReSharper disable once using
                    // ReSharper disable once DisposableConstructor
                    await using var sourceStream = new FileStream(GetLocalFullPath(entry), FileMode.Open,
                        FileAccess.Read, FileShare.Read, CopyBufferSize, true);
                    await using (sourceStream.ConfigureAwait(false))
                    {
                        await sourceStream.CopyToAsync(zipEntryStream, cancellationToken);
                    }
                }
            }
        }
    }

    private static async Task<string> ComputeHash(string filePath, long offset, long length,
        CancellationToken cancellationToken)
    {
        // ReSharper disable once using
        // ReSharper disable once DisposableConstructor
        await using var fileStream =
            new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, true);
        await using (fileStream.ConfigureAwait(false))
        {
            fileStream.Seek(offset, SeekOrigin.Begin);

            // ReSharper disable once using
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyBufferSize];
            long remaining = length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await fileStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                if (read <= 0)
                {
                    throw new UsbCopyJobException($"Unexpected end of file {filePath}");
                }

                incrementalHash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            return Convert.ToHexString(incrementalHash.GetHashAndReset());
        }
    }

    private void CleanupAfterAck(PlannedPackage plannedPackage, PackageSource source)
    {
        try
        {
            switch (plannedPackage.PackageType)
            {
                case EPackageType.Archive:
                    File.Delete(source.FilePath);
                    foreach (RemoteFileEntry entry in plannedPackage.Files)
                    {
                        File.Delete(GetLocalFullPath(entry));
                    }

                    break;
                case EPackageType.WholeFile:
                    File.Delete(source.FilePath);
                    break;
                case EPackageType.FilePart:
                    //დიდი ფაილი მხოლოდ ბოლო ნაწილის დადასტურების შემდეგ იშლება
                    if (plannedPackage.PartIndex == plannedPackage.PartsTotal - 1)
                    {
                        File.Delete(source.FilePath);
                    }

                    break;
                default:
                    throw new SwitchExpressionException();
            }
        }
        catch (IOException e)
        {
            _logger.LogWarning(e, "Cannot cleanup delivered package files for job {JobId}", JobId);
        }
    }

    private Task SendProgress(string message, CancellationToken cancellationToken)
    {
        return _hubContext.Clients.Client(ConnectionId)
            .SendAsync(UsbCopyHubEvents.ReceiveProgress, message, cancellationToken);
    }

    private async Task TrySendFailed(string errorMessage)
    {
        try
        {
            await _hubContext.Clients.Client(ConnectionId)
                .SendAsync(UsbCopyHubEvents.ReceiveJobFailed, errorMessage, CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Cannot send job failure message for job {JobId}", JobId);
        }
    }

    private void CleanupWorkDir()
    {
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, true);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Cannot cleanup work directory {WorkDir}", _workDir);
        }
    }
}
