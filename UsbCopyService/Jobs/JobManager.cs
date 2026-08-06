using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibFileParameters.Models;
using UsbCopyService.Hubs;
using UsbCopyService.Settings;
using UsbCopyServiceShared.Contracts;

namespace UsbCopyService.Jobs;

//მიმდინარე სამუშაოების რეესტრი: თითო SignalR კავშირზე ერთი სამუშაო.
//კავშირის წყვეტა სამუშაოს აღარ კლავს — detach-ის შემდეგ ResumeJob-ით ხელახლა მიბმა შეიძლება,
//სერვისის გადატვირთვის შემდეგ კი სამუშაო დისკზე შენახული state.json-იდან აღდგება
public sealed class JobManager
{
    private readonly IHubContext<UsbCopyHub> _hubContext;
    private readonly ConcurrentDictionary<string, CopyJob> _jobsByConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CopyJob> _jobsById = new(StringComparer.Ordinal);
    private readonly ILogger<JobManager> _logger;
    private readonly UsbCopySettings _settings;

    //მიბმის/მოხსნის/რეგისტრაციის/დასრულების გადასვლების სერიალიზაცია
    private readonly Lock _sync = new();

    // ReSharper disable once ConvertToPrimaryConstructor
    public JobManager(ILogger<JobManager> logger, IHubContext<UsbCopyHub> hubContext, UsbCopySettings settings)
    {
        _logger = logger;
        _hubContext = hubContext;
        _settings = settings;
    }

    public (string? JobId, string? Error) StartJob(string connectionId, string projectName, string[] existingFiles)
    {
        if (string.IsNullOrWhiteSpace(_settings.WorkPath))
        {
            return (null, "WorkPath is not configured on the service side");
        }

        if (!_settings.Projects.TryGetValue(projectName, out UsbCopyServiceProjectSettings? project))
        {
            return (null, $"Project {projectName} is not found on the service side");
        }

        if (string.IsNullOrWhiteSpace(project.FileStorageName))
        {
            return (null, $"File storage is not specified for project {projectName}");
        }

        if (!_settings.FileStorages.TryGetValue(project.FileStorageName, out FileStorageData? fileStorageData))
        {
            return (null, $"File storage {project.FileStorageName} is not found");
        }

        ExcludeSet? excludeSet = null;
        if (!string.IsNullOrWhiteSpace(project.ExcludeSetName) &&
            !_settings.ExcludeSets.TryGetValue(project.ExcludeSetName, out excludeSet))
        {
            return (null, $"Exclude set {project.ExcludeSetName} is not found");
        }

        // ReSharper disable once using
        // ReSharper disable once DisposableConstructor
        var job = new CopyJob(_logger, _hubContext, _settings, connectionId, projectName, fileStorageData, excludeSet,
            existingFiles);

        lock (_sync)
        {
            if (!_jobsByConnection.TryAdd(connectionId, job))
            {
                job.Dispose();
                return (null, "Another job is already running on this connection");
            }

            string jobId = job.JobId;
            _jobsById[jobId] = job;
            job.Start(OnJobFinished);

            return (jobId, null);
        }
    }

    //სამუშაოს ხელახლა მიბმა: ჯერ მეხსიერებაში ვეძებთ, შემდეგ — დისკზე შენახულ მდგომარეობაში
    public ResumeJobResult ResumeJob(string connectionId, string jobId)
    {
        if (!IsValidJobId(jobId))
        {
            return new ResumeJobResult { Status = EResumeJobStatus.NotFound };
        }

        lock (_sync)
        {
            if (_jobsByConnection.TryGetValue(connectionId, out CopyJob? ownedJob) &&
                !string.Equals(ownedJob.JobId, jobId, StringComparison.Ordinal))
            {
                return new ResumeJobResult
                {
                    Status = EResumeJobStatus.Busy, ErrorMessage = "Another job is already running on this connection"
                };
            }

            if (!_jobsById.TryGetValue(jobId, out CopyJob? job) || job.IsFinished)
            {
                return ResumeFromDisk(connectionId, jobId);
            }

            if (!job.TryAttach(connectionId))
            {
                return new ResumeJobResult
                {
                    Status = EResumeJobStatus.Busy,
                    ErrorMessage = "Job is still attached to a previous connection"
                };
            }

            _jobsByConnection[connectionId] = job;
            _logger.LogInformation("Job {JobId} reattached to connection {ConnectionId}", jobId, connectionId);
            return new ResumeJobResult { Status = EResumeJobStatus.Attached };

        }
    }

    private ResumeJobResult ResumeFromDisk(string connectionId, string jobId)
    {
        if (string.IsNullOrWhiteSpace(_settings.WorkPath))
        {
            return new ResumeJobResult
            {
                Status = EResumeJobStatus.Failed, ErrorMessage = "WorkPath is not configured on the service side"
            };
        }

        string workDir = Path.Combine(_settings.WorkPath, "job_" + jobId);
        JobState? state = JobStateStore.TryLoad(workDir);
        if (state is null || !string.Equals(state.JobId, jobId, StringComparison.Ordinal))
        {
            return new ResumeJobResult { Status = EResumeJobStatus.NotFound };
        }

        FileStorageData? fileStorageData = null;
        ExcludeSet? excludeSet = null;
        if (_settings.Projects.TryGetValue(state.ProjectName, out UsbCopyServiceProjectSettings? project))
        {
            if (!string.IsNullOrWhiteSpace(project.FileStorageName))
            {
                _settings.FileStorages.TryGetValue(project.FileStorageName, out fileStorageData);
            }

            if (!string.IsNullOrWhiteSpace(project.ExcludeSetName))
            {
                _settings.ExcludeSets.TryGetValue(project.ExcludeSetName, out excludeSet);
            }
        }

        //Staging ფაზის გასაგრძელებლად წყაროს კონფიგურაცია აუცილებელია; Transferring ფაზა მის გარეშეც სრულდება
        if (state.Phase == EJobPhase.Staging && fileStorageData is null)
        {
            return new ResumeJobResult
            {
                Status = EResumeJobStatus.Failed,
                ErrorMessage = $"Project {state.ProjectName} is no longer configured on the service side"
            };
        }

        // ReSharper disable once using
        // ReSharper disable once DisposableConstructor
        var job = new CopyJob(_logger, _hubContext, _settings, connectionId, state, fileStorageData, excludeSet);

        _jobsById[jobId] = job;
        _jobsByConnection[connectionId] = job;
        job.Start(OnJobFinished);

        _logger.LogInformation(
            "Job {JobId} resumed from disk by connection {ConnectionId} (phase {Phase}, package {NextPackageIndex}/{PackagesTotal})",
            jobId, connectionId, state.Phase, state.NextPackageIndex, state.PackagesTotal);
        return new ResumeJobResult { Status = EResumeJobStatus.Attached };
    }

    //jobId ყოველთვის Guid("N") ფორმატისაა — სხვა მნიშვნელობა გზების ასაწყობად საშიშია და უარიყოფა
    private static bool IsValidJobId(string jobId)
    {
        return jobId.Length == 32 && jobId.All(char.IsAsciiHexDigitLower);
    }

    private void OnJobFinished(CopyJob job)
    {
        lock (_sync)
        {
            //კავშირის იდენტიფიკატორი დასრულების მომენტში იკითხება — reattach-ის შემდეგ საწყისი აღარ ემთხვევა
            string? connectionId = job.CurrentConnectionId;
            if (connectionId is not null)
            {
                _jobsByConnection.TryRemove(new KeyValuePair<string, CopyJob>(connectionId, job));
            }

            _jobsById.TryRemove(new KeyValuePair<string, CopyJob>(job.JobId, job));
        }
    }

    public void AckPackage(string connectionId, string jobId, string packageId, bool ok, string? errorMessage)
    {
        if (!_jobsById.TryGetValue(jobId, out CopyJob? job))
        {
            _logger.LogWarning("Ack for unknown job {JobId} ignored", jobId);
            return;
        }

        //დასტურის მიღება მხოლოდ სამუშაოს მფლობელი კავშირიდან შეიძლება
        if (!string.Equals(job.CurrentConnectionId, connectionId, StringComparison.Ordinal))
        {
            _logger.LogWarning("Ack for job {JobId} from foreign connection ignored", jobId);
            return;
        }

        job.SetAck(packageId, ok, errorMessage);
    }

    //კავშირის წყვეტისას სამუშაო არ უქმდება — მხოლოდ იხსნება კავშირიდან და TTL-ის ვადაში აღდგენას ელოდება
    public void DetachForConnection(string connectionId)
    {
        lock (_sync)
        {
            if (!_jobsByConnection.TryRemove(connectionId, out CopyJob? job))
            {
                return;
            }

            _logger.LogInformation("Connection {ConnectionId} disconnected, detaching job {JobId}", connectionId,
                job.JobId);
            job.Detach();
        }
    }

    public PackageSource? ResolvePackage(string jobId, string packageId)
    {
        return _jobsById.TryGetValue(jobId, out CopyJob? job) ? job.GetCurrentPackage(packageId) : null;
    }
}
