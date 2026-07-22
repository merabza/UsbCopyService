using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using ParametersManagement.LibFileParameters.Models;
using UsbCopyService.Hubs;
using UsbCopyService.Settings;

namespace UsbCopyService.Jobs;

//მიმდინარე სამუშაოების რეესტრი: თითო SignalR კავშირზე ერთი სამუშაო
public sealed class JobManager
{
    private readonly IHubContext<UsbCopyHub> _hubContext;
    private readonly ConcurrentDictionary<string, CopyJob> _jobsByConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CopyJob> _jobsById = new(StringComparer.Ordinal);
    private readonly ILogger<JobManager> _logger;
    private readonly UsbCopySettings _settings;

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

        var job = new CopyJob(_logger, _hubContext, _settings, connectionId, projectName, fileStorageData, excludeSet,
            existingFiles);

        if (!_jobsByConnection.TryAdd(connectionId, job))
        {
            job.Dispose();
            return (null, "Another job is already running on this connection");
        }

        string jobId = job.JobId;
        _jobsById[jobId] = job;
        job.Start(finishedJob => OnJobFinished(connectionId, finishedJob));

        return (jobId, null);
    }

    private void OnJobFinished(string connectionId, CopyJob job)
    {
        _jobsByConnection.TryRemove(new KeyValuePair<string, CopyJob>(connectionId, job));
        _jobsById.TryRemove(new KeyValuePair<string, CopyJob>(job.JobId, job));
    }

    public void AckPackage(string connectionId, string jobId, string packageId, bool ok, string? errorMessage)
    {
        if (!_jobsById.TryGetValue(jobId, out CopyJob? job))
        {
            _logger.LogWarning("Ack for unknown job {JobId} ignored", jobId);
            return;
        }

        //დასტურის მიღება მხოლოდ სამუშაოს მფლობელი კავშირიდან შეიძლება
        if (!string.Equals(job.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            _logger.LogWarning("Ack for job {JobId} from foreign connection ignored", jobId);
            return;
        }

        job.SetAck(packageId, ok, errorMessage);
    }

    public void CancelForConnection(string connectionId)
    {
        if (_jobsByConnection.TryGetValue(connectionId, out CopyJob? job))
        {
            string jobId = job.JobId;
            _logger.LogInformation("Connection {ConnectionId} disconnected, canceling job {JobId}", connectionId,
                jobId);
            job.Cancel();
        }
    }

    public PackageSource? ResolvePackage(string jobId, string packageId)
    {
        return _jobsById.TryGetValue(jobId, out CopyJob? job) ? job.GetCurrentPackage(packageId) : null;
    }
}
