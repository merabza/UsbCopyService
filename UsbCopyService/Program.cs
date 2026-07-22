using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using UsbCopyService.DependencyInjection;
using UsbCopyService.Endpoints;
using UsbCopyService.Jobs;
using UsbCopyService.Settings;
using WebSystemTools.ApiExceptionHandler.DependencyInjection;
using WebSystemTools.ApiKeyIdentity.DependencyInjection;
using WebSystemTools.SerilogLogger;
using WebSystemTools.TestToolsApi.DependencyInjection;
using WebSystemTools.WindowsServiceTools;

try
{
    Console.WriteLine("UsbCopy Service Loading...");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        ContentRootPath = AppContext.BaseDirectory, Args = args
    });

    bool debugMode = builder.Environment.IsDevelopment();

    ILogger logger = builder.Host.UseSerilogLogger(debugMode, builder.Configuration);
    ILogger? debugLogger = debugMode ? logger : null;
    builder.Host.UseWindowsServiceOnWindows(debugLogger, args);

    builder.Services.AddUsbCopyServices(debugLogger, builder.Configuration);

    // ReSharper disable once using
    await using WebApplication app = builder.Build();
    app.UseApiKeysAuthorization(debugLogger);
    app.UseTestToolsApiEndpoints(debugLogger);
    app.UseUsbCopyApiEndpoints(debugLogger);
    app.UseApiExceptionHandler(debugLogger);

    WorkPathPreparer.Prepare(app.Services.GetRequiredService<UsbCopySettings>(), logger);

    await app.RunAsync();
    return 0;
}
catch (Exception e)
{
    Log.Fatal(e, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
