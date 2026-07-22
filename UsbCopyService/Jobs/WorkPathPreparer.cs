using System;
using System.IO;
using UsbCopyService.Settings;

namespace UsbCopyService.Jobs;

//სერვისის გაშვებისას სამუშაო საქაღალდეში დარჩენილი ძველი job_* ქვესაქაღალდეების გასუფთავება
public static class WorkPathPreparer
{
    public static void Prepare(UsbCopySettings settings, Serilog.ILogger logger)
    {
        if (!settings.CleanWorkPathOnStart)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.WorkPath) || !Path.IsPathRooted(settings.WorkPath))
        {
            logger.Warning("WorkPath is not configured or is not rooted, cleanup skipped");
            return;
        }

        string fullWorkPath = Path.GetFullPath(settings.WorkPath);
        if (string.Equals(fullWorkPath, Path.GetPathRoot(fullWorkPath), StringComparison.OrdinalIgnoreCase))
        {
            logger.Warning("WorkPath must not be a drive root, cleanup skipped");
            return;
        }

        if (!Directory.Exists(fullWorkPath))
        {
            return;
        }

        foreach (string jobDir in Directory.GetDirectories(fullWorkPath, "job_*"))
        {
            try
            {
                Directory.Delete(jobDir, true);
                logger.Information("Removed stale job work directory {JobDir}", jobDir);
            }
            catch (Exception e)
            {
                logger.Warning(e, "Cannot remove stale job work directory {JobDir}", jobDir);
            }
        }
    }
}
