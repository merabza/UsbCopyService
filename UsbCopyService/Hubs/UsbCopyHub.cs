using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using UsbCopyService.Jobs;

namespace UsbCopyService.Hubs;

//კლიენტთან მუდმივი კავშირის ჰაბი: ბრძანებები კლიენტიდან, პროგრესი და პაკეტების შეტყობინებები სერვისიდან
public sealed class UsbCopyHub : Hub
{
    private readonly JobManager _jobManager;
    private readonly ILogger<UsbCopyHub> _logger;

    // ReSharper disable once ConvertToPrimaryConstructor
    public UsbCopyHub(JobManager jobManager, ILogger<UsbCopyHub> logger)
    {
        _jobManager = jobManager;
        _logger = logger;
    }

    public Task<string> StartJob(string projectName, string[]? existingFiles)
    {
        string connectionId = Context.ConnectionId;
        _logger.LogInformation("StartJob {ProjectName} requested by connection {ConnectionId}", projectName,
            connectionId);

        (string? jobId, string? error) = _jobManager.StartJob(connectionId, projectName, existingFiles ?? []);

        if (jobId is null)
        {
            throw new HubException(error ?? "StartJob failed");
        }

        return Task.FromResult(jobId);
    }

    public Task AckPackage(string jobId, string packageId, bool ok, string? errorMessage)
    {
        _jobManager.AckPackage(Context.ConnectionId, jobId, packageId, ok, errorMessage);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _jobManager.CancelForConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
