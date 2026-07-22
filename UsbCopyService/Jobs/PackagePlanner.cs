using System;
using System.Collections.Generic;
using System.Linq;
using UsbCopyServiceShared.Contracts;

namespace UsbCopyService.Jobs;

//შერჩეული ფაილების დაყოფა გადასაცემ პაკეტებად:
//პატარები ერთიანდება zip არქივებში, საშუალოები მთლიანად გადაიცემა, დიდები ნაწილდება მონაკვეთებად
public static class PackagePlanner
{
    public static List<PlannedPackage> Plan(IReadOnlyCollection<RemoteFileEntry> files, long smallFileMaxSize,
        long archiveVolumeMaxSize, long partMaxSize)
    {
        List<PlannedPackage> result = [];

        List<RemoteFileEntry> ordered =
            files.OrderBy(o => o.WirePath, StringComparer.OrdinalIgnoreCase).ToList();

        result.AddRange(PlanArchives(ordered.Where(f => f.FileLength <= smallFileMaxSize), archiveVolumeMaxSize));

        result.AddRange(ordered.Where(f => f.FileLength > smallFileMaxSize && f.FileLength <= partMaxSize).Select(f =>
            new PlannedPackage(EPackageType.WholeFile, [f], 0, 0, 0, f.FileLength)));

        foreach (RemoteFileEntry bigFile in ordered.Where(f => f.FileLength > partMaxSize))
        {
            result.AddRange(PlanParts(bigFile, partMaxSize));
        }

        return result;
    }

    private static List<PlannedPackage> PlanArchives(IEnumerable<RemoteFileEntry> smallFiles, long archiveVolumeMaxSize)
    {
        List<PlannedPackage> result = [];
        List<RemoteFileEntry> currentVolume = [];
        long currentVolumeSize = 0;

        foreach (RemoteFileEntry file in smallFiles)
        {
            if (currentVolume.Count > 0 && currentVolumeSize + file.FileLength > archiveVolumeMaxSize)
            {
                result.Add(new PlannedPackage(EPackageType.Archive, currentVolume, 0, 0, 0, 0));
                currentVolume = [];
                currentVolumeSize = 0;
            }

            currentVolume.Add(file);
            currentVolumeSize += file.FileLength;
        }

        if (currentVolume.Count > 0)
        {
            result.Add(new PlannedPackage(EPackageType.Archive, currentVolume, 0, 0, 0, 0));
        }

        return result;
    }

    private static List<PlannedPackage> PlanParts(RemoteFileEntry bigFile, long partMaxSize)
    {
        List<PlannedPackage> result = [];
        var partsTotal = (int)((bigFile.FileLength + partMaxSize - 1) / partMaxSize);

        for (var partIndex = 0; partIndex < partsTotal; partIndex++)
        {
            long partOffset = partIndex * partMaxSize;
            long partLength = Math.Min(partMaxSize, bigFile.FileLength - partOffset);
            result.Add(new PlannedPackage(EPackageType.FilePart, [bigFile], partIndex, partsTotal, partOffset,
                partLength));
        }

        return result;
    }
}
