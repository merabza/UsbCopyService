using System.Collections.Generic;
using System.Linq;
using UsbCopyServiceShared.Contracts;

namespace UsbCopyService.Jobs;

//PlannedPackage-ის შესანახი ასლი state.json-ისთვის
public sealed class PlannedPackageState
{
    public EPackageType PackageType { get; set; }

    public List<RemoteFileEntryState> Files { get; set; } = [];

    public int PartIndex { get; set; }

    public int PartsTotal { get; set; }

    public long PartOffset { get; set; }

    public long PartLength { get; set; }

    public static PlannedPackageState From(PlannedPackage plannedPackage)
    {
        return new PlannedPackageState
        {
            PackageType = plannedPackage.PackageType,
            Files = [.. plannedPackage.Files.Select(RemoteFileEntryState.From)],
            PartIndex = plannedPackage.PartIndex,
            PartsTotal = plannedPackage.PartsTotal,
            PartOffset = plannedPackage.PartOffset,
            PartLength = plannedPackage.PartLength
        };
    }

    public PlannedPackage ToPlannedPackage()
    {
        return new PlannedPackage(PackageType, [.. Files.Select(f => f.ToRemoteFileEntry())], PartIndex, PartsTotal,
            PartOffset, PartLength);
    }
}
