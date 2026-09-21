using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using UsbCopyService.Jobs;
using UsbCopyServiceShared.Contracts;

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

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("StartJob {ProjectName} requested by connection {ConnectionId}", projectName,
                connectionId);
        }

        (string? jobId, string? error) = _jobManager.StartJob(connectionId, projectName, existingFiles ?? []);

        if (jobId is null)
        {
            throw new HubException(error ?? "StartJob failed");
        }

        return Task.FromResult(jobId);
    }

    //სამუშაოს ხელახლა მიბმა კავშირის წყვეტის ან სერვისის გადატვირთვის შემდეგ; პასუხი ResumeJobResult-ის JSON-ია
    public Task<string> ResumeJob(string jobId)
    {
        string connectionId = Context.ConnectionId;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("ResumeJob {JobId} requested by connection {ConnectionId}", jobId, connectionId);
        }

        ResumeJobResult result = _jobManager.ResumeJob(connectionId, jobId);
        return Task.FromResult(JsonSerializer.Serialize(result));
    }

    public Task AckPackage(string jobId, string packageId, bool ok, string? errorMessage)
    {
        _jobManager.AckPackage(Context.ConnectionId, jobId, packageId, ok, errorMessage);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _jobManager.DetachForConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
