using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UsbCopyService.Hubs;
using UsbCopyService.Jobs;
using UsbCopyServiceShared.Contracts.V1.Routes;

namespace UsbCopyService.Endpoints;

public static class UsbCopyEndpoints
{
    private const int CopyBufferSize = 81920;

    public static bool UseUsbCopyApiEndpoints(this IEndpointRouteBuilder endpoints, Serilog.ILogger? debugLogger)
    {
        debugLogger?.Information("UseUsbCopyApiEndpoints");

        RouteGroupBuilder group = endpoints
            .MapGroup(UsbCopyApiRoutes.ApiBase + UsbCopyApiRoutes.UsbCopyRoute.UsbCopyBase).RequireAuthorization();

        group.MapHub<UsbCopyHub>(UsbCopyApiRoutes.UsbCopyRoute.Hub);
        group.MapGet(UsbCopyApiRoutes.UsbCopyRoute.Download + "/{jobId}/{packageId}", DownloadPackage);

        return true;
    }

    //მიმდინარე პაკეტის ბაიტების გადაცემა კლიენტთან ნაკადის სახით
    private static async Task DownloadPackage(HttpContext httpContext, JobManager jobManager, string jobId,
        string packageId)
    {
        PackageSource? packageSource = jobManager.ResolvePackage(jobId, packageId);
        if (packageSource is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        httpContext.Response.ContentType = "application/octet-stream";
        httpContext.Response.ContentLength = packageSource.Length;

        var fileStream = new FileStream(packageSource.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, true);
        await using (fileStream.ConfigureAwait(false))
        {
            fileStream.Seek(packageSource.Offset, SeekOrigin.Begin);

            var buffer = new byte[CopyBufferSize];
            long remaining = packageSource.Length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await fileStream.ReadAsync(buffer.AsMemory(0, toRead), httpContext.RequestAborted);
                if (read <= 0)
                {
                    break;
                }

                await httpContext.Response.Body.WriteAsync(buffer.AsMemory(0, read), httpContext.RequestAborted);
                remaining -= read;
            }
        }
    }
}
