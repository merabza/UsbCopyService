using System;
using System.Collections.Generic;
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

//ერთი კლიენტის მოთხოვნით გაშვებული სამუშაო: წყაროდან ფაილების ჩამოტვირთვა, ოპტიმიზაცია და კლიენტისთვის მიწოდება.
//მდგომარეობა ინახება დისკზე (state.json), ამიტომ სამუშაო კავშირის წყვეტასაც და სერვისის გადატვირთვასაც უძლებს
public sealed class CopyJob : IDisposable
{
    internal const string DownloadTempExtension = "dwn";
    private const int CopyBufferSize = 81920;

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly object _connectionLock = new();
    private readonly ExcludeSet? _excludeSet;
    private readonly FileStorageData? _fileStorageData;
    private readonly IHubContext<UsbCopyHub> _hubContext;
    private readonly ILogger _logger;
    private readonly string _outDir;
    private readonly UsbCopySettings _settings;
    private readonly string _srcDir;
    private readonly JobState _state;
    private readonly string _workDir;

    private TaskCompletionSource _attachedTcs;

    private string? _connectionId;

    private volatile PackageCurrent? _current;
    private TaskCompletionSource _detachedTcs;

    private volatile bool _isFinished;

    //ახალი სამუშაოს კონსტრუქტორი
    public CopyJob(ILogger logger, IHubContext<UsbCopyHub> hubContext, UsbCopySettings settings, string connectionId,
        string projectName, FileStorageData fileStorageData, ExcludeSet? excludeSet, string[] existingFiles) : this(
        logger, hubContext, settings, connectionId, new JobState
        {
            JobId = Guid.NewGuid().ToString("N"),
            ProjectName = projectName,
            ExistingFiles = existingFiles,
            Phase = EJobPhase.Staging,
            CreatedAtUtc = DateTime.UtcNow
        }, fileStorageData, excludeSet)
    {
    }

    //აღდგენილი სამუშაოს კონსტრუქტორი: მდგომარეობა დისკიდან იკითხება; Transferring ფაზას წყაროს კონფიგურაცია აღარ სჭირდება
    public CopyJob(ILogger logger, IHubContext<UsbCopyHub> hubContext, UsbCopySettings settings, string connectionId,
        JobState state, FileStorageData? fileStorageData, ExcludeSet? excludeSet)
    {
        _logger = logger;
        _hubContext = hubContext;
        _settings = settings;
        _connectionId = connectionId;
        _fileStorageData = fileStorageData;
        _excludeSet = excludeSet;
        _state = state;

        _attachedTcs = NewTcs();
        _attachedTcs.TrySetResult();
        _detachedTcs = NewTcs();

        _workDir = Path.Combine(settings.WorkPath ?? string.Empty, "job_" + state.JobId);
        _srcDir = Path.Combine(_workDir, "src");
        _outDir = Path.Combine(_workDir, "out");
    }

    public string JobId => _state.JobId;

    //კავშირი, რომელზეც სამუშაოა მიბმული; null ნიშნავს, რომ კლიენტი გათიშულია (detached)
    public string? CurrentConnectionId
    {
        get
        {
            lock (_connectionLock)
            {
                return _connectionId;
            }
        }
    }

    //სამუშაო დასრულებულია და მისი ხელახლა მიბმა აღარ შეიძლება
    public bool IsFinished => _isFinished;

    public void Dispose()
    {
        _cancellationTokenSource.Dispose();
    }

    private static TaskCompletionSource NewTcs()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    //სამუშაოს ფონურად გაშვება; დასრულებისას onFinished ეძახება რეესტრიდან ამოსაშლელად.
    //Task.Run აუცილებელია: async მეთოდი პირველ await-მდე გამომძახებლის ნაკადზე ირბენს და Task.Run-ის გარეშე
    //სინქრონული staging სამუშაოები ჰაბის მეთოდს (და JobManager-ის ლოკს) დააკავებდა — StartJob-ის პასუხი კლიენტს ვერ მიუვიდოდა
    public void Start(Action<CopyJob> onFinished)
    {
        //ტოკენი Task.Run-ს განზრახ არ გადაეცემა: გაშვებამდე გაუქმებული ტოკენი RunAndCleanup-ს (და მის finally-ში
        //onFinished/Dispose-ს) საერთოდ არ გაუშვებდა და სამუშაო რეესტრში ჩარჩებოდა; გაუქმებას RunAsync შიგნით ამუშავებს
        _ = Task.Run(() => RunAndCleanup(onFinished), CancellationToken.None);
    }

    private async Task RunAndCleanup(Action<CopyJob> onFinished)
    {
        try
        {
            await RunAsync();
        }
        finally
        {
            _isFinished = true;
            onFinished(this);
            Dispose();
        }
    }

    //კავშირის მოხსნა: სამუშაო არ უქმდება — ჩერდება და TTL-ის ვადაში ხელახლა მიბმას ელოდება
    public void Detach()
    {
        lock (_connectionLock)
        {
            if (_isFinished)
            {
                return;
            }

            _connectionId = null;

            try
            {
                _cancellationTokenSource.CancelAfter(_settings.DetachedJobTtl);
            }
            catch (ObjectDisposedException)
            {
                //სამუშაო უკვე დასრულებულია
                return;
            }

            _detachedTcs.TrySetResult();
            _attachedTcs = NewTcs();
        }
    }

    //სამუშაოს მიბმა ახალ კავშირზე; false ბრუნდება, თუ სამუშაო ჯერ კიდევ სხვა კავშირს უჭირავს ან დასრულებულია
    public bool TryAttach(string connectionId)
    {
        lock (_connectionLock)
        {
            if (_isFinished || _cancellationTokenSource.IsCancellationRequested)
            {
                return false;
            }

            if (string.Equals(_connectionId, connectionId, StringComparison.Ordinal))
            {
                return true;
            }

            if (_connectionId is not null)
            {
                return false;
            }

            _connectionId = connectionId;

            try
            {
                //TTL-ის გამორთვა — კლიენტი ისევ ჩვენთანაა
                _cancellationTokenSource.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            _attachedTcs.TrySetResult();
            _detachedTcs = NewTcs();
            return true;
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
        try
        {
            await SendProgress($"Job {JobId} for project {_state.ProjectName} started", cancellationToken);

            List<PlannedPackage> plan = _state.Phase == EJobPhase.Staging
                ? await PrepareStagingAndPlan(cancellationToken)
                : RestorePlanFromState();

            await ServePackages(plan, cancellationToken);

            var summary = new JobSummary
            {
                FilesTotal = _state.FilesTotal,
                FilesSkipped = _state.FilesSkipped,
                BytesOriginal = _state.BytesOriginal,
                BytesTransferred = _state.BytesTransferred,
                PackagesTotal = _state.PackagesTotal,
                ElapsedSeconds = (DateTime.UtcNow - _state.CreatedAtUtc).TotalSeconds
            };

            //ჯერ მდგომარეობა და სამუშაო საქაღალდე იშლება, მერე იგზავნება დასრულების შეტყობინება:
            //თუ შუაში მოვკვდით, კლიენტი ResumeJob-ზე NotFound-ს მიიღებს და ახალი (ცარიელი) სამუშაოთი დაასრულებს
            CleanupWorkDir();
            await SafeSendToClient(CurrentConnectionId, UsbCopyHubEvents.ReceiveJobCompleted,
                JsonSerializer.Serialize(summary), CancellationToken.None);
            _logger.LogInformation("Job {JobId} completed in {Elapsed} seconds", JobId, summary.ElapsedSeconds);
        }
        catch (OperationCanceledException e)
        {
            //ერთადერთი გაუქმების წყარო detached TTL-ის ამოწურვაა — მიტოვებული სამუშაო იშლება
            _logger.LogInformation(e, "Job {JobId} abandoned: client did not reconnect within {TtlMinutes} minutes",
                JobId, _settings.DetachedJobTtlMinutes);
            CleanupWorkDir();
            await TrySendFailed("Job was abandoned: client did not reconnect in time");
        }
        catch (UsbCopyJobException e)
        {
            //მდგომარეობა და staging რჩება — კლიენტს ResumeJob-ით გაგრძელება შეეძლება
            _logger.LogError(e, "Job {JobId} failed", JobId);
            await TrySendFailed(e.Message);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Job {JobId} failed unexpectedly", JobId);
            await TrySendFailed(e.Message);
        }
    }

    //Staging ფაზა: წყაროს დათვალიერება, ფაილების ჩამოტვირთვა (უკვე ჩამოტვირთულების გამოტოვებით) და გეგმის აგება
    private async Task<List<PlannedPackage>> PrepareStagingAndPlan(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_outDir);
        JobStateStore.Save(_workDir, _state);

        (FileManager fileManager, WalkResult walkResult) = await CollectFiles(cancellationToken);

        _state.FilesTotal = walkResult.Files.Count;
        _state.FilesSkipped = walkResult.SkippedExisting;
        _state.BytesOriginal = walkResult.Files.Sum(f => f.FileLength);

        await SendProgress(
            $"Selected {_state.FilesTotal} files ({_state.BytesOriginal} bytes), skipped {_state.FilesSkipped} existing",
            cancellationToken);

        List<PlannedPackage> plan = [];
        if (walkResult.Files.Count > 0)
        {
            //აღდგენისას უკვე ჩამოტვირთული (ზომით დამთხვეული) ფაილები ხელახლა აღარ მოგვაქვს
            List<RemoteFileEntry> filesToDownload = [.. walkResult.Files.Where(f => !IsAlreadyStaged(f))];

            CheckFreeSpace(filesToDownload);

            await DownloadAll(fileManager, filesToDownload, cancellationToken);

            plan = PackagePlanner.Plan(walkResult.Files, _settings.SmallFileMaxSizeBytes,
                _settings.ArchiveVolumeMaxSizeBytes, _settings.PartMaxSizeBytes);
        }

        _state.Plan = [.. plan.Select(PlannedPackageState.From)];
        _state.Phase = EJobPhase.Transferring;
        _state.PackagesTotal = plan.Count;
        _state.NextPackageIndex = 0;
        JobStateStore.Save(_workDir, _state);

        await SendProgress($"Prepared {plan.Count} packages", cancellationToken);
        return plan;
    }

    //Transferring ფაზის აღდგენა: გეგმა დისკიდან იკითხება და მოწმდება, რომ დარჩენილი პაკეტების staged ფაილები ადგილზეა
    private List<PlannedPackage> RestorePlanFromState()
    {
        List<PlannedPackage> plan = [.. (_state.Plan ?? []).Select(s => s.ToPlannedPackage())];

        for (int packageIndex = _state.NextPackageIndex; packageIndex < plan.Count; packageIndex++)
        {
            foreach (RemoteFileEntry entry in plan[packageIndex].Files)
            {
                string localFullPath = GetLocalFullPath(entry);
                if (File.Exists(localFullPath) && new FileInfo(localFullPath).Length == entry.FileLength)
                {
                    continue;
                }

                //staged მონაცემები დაკარგულია — სამუშაო აღდგენადი აღარ არის, კლიენტმა ახალი უნდა დაიწყოს
                CleanupWorkDir();
                throw new UsbCopyJobException($"Staged data lost for {entry.WirePath}, a new job must be started");
            }
        }

        CleanupCompletedPackages(plan);

        _logger.LogInformation("Job {JobId} restored from disk at package {NextPackageIndex}/{PackagesTotal}", JobId,
            _state.NextPackageIndex, _state.PackagesTotal);
        return plan;
    }

    //წყვეტის გამო შესაძლოა დადასტურებული პაკეტების staged ფაილები ვერ წაიშალა — საუკეთესო ძალისხმევით ვასუფთავებთ
    private void CleanupCompletedPackages(List<PlannedPackage> plan)
    {
        for (var packageIndex = 0; packageIndex < _state.NextPackageIndex && packageIndex < plan.Count; packageIndex++)
        {
            PlannedPackage plannedPackage = plan[packageIndex];
            try
            {
                switch (plannedPackage.PackageType)
                {
                    case EPackageType.Archive:
                        File.Delete(GetZipPath(packageIndex));
                        foreach (RemoteFileEntry entry in plannedPackage.Files)
                        {
                            File.Delete(GetLocalFullPath(entry));
                        }

                        break;
                    case EPackageType.WholeFile:
                        File.Delete(GetLocalFullPath(plannedPackage.Files[0]));
                        break;
                    case EPackageType.FilePart:
                        //დიდი ფაილი მხოლოდ მაშინ იშლება, როცა მისი ბოლო ნაწილიც დადასტურებულია
                        if (plannedPackage.PartIndex == plannedPackage.PartsTotal - 1)
                        {
                            File.Delete(GetLocalFullPath(plannedPackage.Files[0]));
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
    }

    private bool IsAlreadyStaged(RemoteFileEntry entry)
    {
        string localFullPath = GetLocalFullPath(entry);
        return File.Exists(localFullPath) && new FileInfo(localFullPath).Length == entry.FileLength;
    }

    private async Task<(FileManager FileManager, WalkResult WalkResult)> CollectFiles(
        CancellationToken cancellationToken)
    {
        if (_fileStorageData is null)
        {
            throw new UsbCopyJobException($"File storage for project {_state.ProjectName} is not configured");
        }

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

        var existingFiles = new HashSet<string>(_state.ExistingFiles, StringComparer.OrdinalIgnoreCase);
        var walker = new RemoteTreeWalker(fileManager, excludes, existingFiles,
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

            //წყვეტისას დარჩენილი შუალედური (.dwn) ფაილი ხელახლა ჩამოტვირთვას "already exists" შეცდომით ჩააგდებდა
            DeleteStaleDownloadTemp(entry, isDiskSource);

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

    private void DeleteStaleDownloadTemp(RemoteFileEntry entry, bool isDiskSource)
    {
        try
        {
            File.Delete(GetLocalFullPath(entry) + "." + DownloadTempExtension);

            //DiskFileManager შუალედურ ფაილს წყაროს გვერდით ქმნის
            if (isDiskSource && !string.IsNullOrWhiteSpace(_fileStorageData?.FileStoragePath))
            {
                File.Delete(Path.Combine(_fileStorageData.FileStoragePath,
                    entry.WirePath.Replace('/', Path.DirectorySeparatorChar)) + "." + DownloadTempExtension);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Cannot delete stale download temp for {WirePath} in job {JobId}", entry.WirePath,
                JobId);
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

    private string GetZipPath(int packageIndex)
    {
        return Path.Combine(_outDir, "package_" + packageIndex.ToString("D4", CultureInfo.InvariantCulture) + ".zip");
    }

    private async Task ServePackages(List<PlannedPackage> plan, CancellationToken cancellationToken)
    {
        for (int packageIndex = _state.NextPackageIndex; packageIndex < plan.Count; packageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PlannedPackage plannedPackage = plan[packageIndex];
            (PackageSource source, PackageManifest manifest) =
                await PreparePackage(plannedPackage, packageIndex, plan.Count, cancellationToken);

            var ackTcs = new TaskCompletionSource<AckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _current = new PackageCurrent(source, ackTcs);

            AckResult ackResult = await OfferAndWaitAck(manifest, ackTcs, cancellationToken);
            _current = null;

            if (!ackResult.Ok)
            {
                //კლიენტის უარი ნიშნავს, რომ პაკეტი ვერასდროს დამუშავდება — ამ სამუშაოს გაგრძელება უაზროა,
                //მდგომარეობა იშლება, რომ შემდეგი გაშვება სუფთა სამუშაოთი დაიწყოს
                CleanupWorkDir();
                throw new UsbCopyJobException(
                    $"Client rejected package {manifest.PackageId}: {ackResult.ErrorMessage ?? "unknown error"}");
            }

            //ჯერ მდგომარეობა ინახება, მერე იშლება staged ფაილები: წყვეტის შემთხვევაში ზედმეტი ფაილი დარჩება და არა პირიქით
            _state.BytesTransferred += source.Length;
            _state.NextPackageIndex = packageIndex + 1;
            JobStateStore.Save(_workDir, _state);

            CleanupAfterAck(plannedPackage, source);

            await SendProgress($"Package {packageIndex + 1}/{plan.Count} delivered", cancellationToken);
        }
    }

    //პაკეტის შეთავაზება და დასტურის მოლოდინი; კავშირის წყვეტისას პაკეტი ჩერდება და ხელახლა მიბმისას თავიდან თავაზდება
    private async Task<AckResult> OfferAndWaitAck(PackageManifest manifest, TaskCompletionSource<AckResult> ackTcs,
        CancellationToken cancellationToken)
    {
        string manifestJson = JsonSerializer.Serialize(manifest);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? connectionId;
            Task attachedTask;
            Task detachedTask;
            lock (_connectionLock)
            {
                connectionId = _connectionId;
                attachedTask = _attachedTcs.Task;
                detachedTask = _detachedTcs.Task;
            }

            if (connectionId is null)
            {
                //კლიენტი გათიშულია — ველოდებით ხელახლა მიბმას; TTL-ის ამოწურვა OperationCanceledException-ს ისვრის
                await attachedTask.WaitAsync(cancellationToken);
                continue;
            }

            //მანიფესტი იგზავნება ყოველ მიბმაზე, რომ reconnect-ის შემდეგ კლიენტმა ის თავიდან მიიღოს
            await SafeSendToClient(connectionId, UsbCopyHubEvents.ReceivePackageReady, manifestJson, cancellationToken);

            // ReSharper disable once using
            // ReSharper disable once DisposableConstructor
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task timeoutTask = Task.Delay(TimeSpan.FromMinutes(_settings.AckTimeoutMinutes), timeoutCts.Token);

            Task completedTask = await Task.WhenAny(ackTcs.Task, detachedTask, timeoutTask);

            if (completedTask == ackTcs.Task)
            {
                await timeoutCts.CancelAsync();
                return await ackTcs.Task;
            }

            if (completedTask == detachedTask)
            {
                //კლიენტი მოლოდინისას გაითიშა — ვბრუნდებით პარკირებაში
                await timeoutCts.CancelAsync();
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
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
                string zipPath = GetZipPath(packageIndex);
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
        return SafeSendToClient(CurrentConnectionId, UsbCopyHubEvents.ReceiveProgress, message, cancellationToken);
    }

    //გაგზავნა კლიენტთან: გათიშულ მდგომარეობაში შეტყობინება უბრალოდ იკარგება, გაგზავნის შეცდომა სამუშაოს არ აჩერებს
    private async Task SafeSendToClient(string? connectionId, string method, string payload,
        CancellationToken cancellationToken)
    {
        if (connectionId is null)
        {
            return;
        }

        try
        {
            await _hubContext.Clients.Client(connectionId).SendAsync(method, payload, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Cannot send {Method} message for job {JobId}", method, JobId);
        }
    }

    private async Task TrySendFailed(string errorMessage)
    {
        await SafeSendToClient(CurrentConnectionId, UsbCopyHubEvents.ReceiveJobFailed, errorMessage,
            CancellationToken.None);
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
