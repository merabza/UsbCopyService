using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UsbCopyService.Jobs;
using UsbCopyService.Settings;
using WebSystemTools.ApiKeyIdentity.DependencyInjection;

namespace UsbCopyService.DependencyInjection;

public static class UsbCopyServiceDependencyInjection
{
    //existingFiles სიის მისაღებად საკმარისზე დიდი ზღვარი
    private const long MaximumReceiveMessageSize = 64L * 1024L * 1024L;

    public static IServiceCollection AddUsbCopyServices(this IServiceCollection services,
        Serilog.ILogger? debugLogger, IConfiguration configuration)
    {
        debugLogger?.Information("AddUsbCopyServices");

        UsbCopySettings settings = configuration.GetSection(UsbCopySettings.SectionName).Get<UsbCopySettings>() ??
                                   new UsbCopySettings();

        services.AddSingleton(settings);
        services.AddSingleton<JobManager>();
        services.AddApiKeyIdentity(debugLogger);
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = MaximumReceiveMessageSize;
            options.EnableDetailedErrors = true;
        }).AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNamingPolicy = null);

        return services;
    }
}
