using System.Collections.Generic;
using UsbCopyServiceShared.Contracts;

namespace UsbCopyService.Jobs;

//ერთი გადასაცემი პაკეტის გეგმა
public sealed class PlannedPackage
{
    // ReSharper disable once ConvertToPrimaryConstructor
    public PlannedPackage(EPackageType packageType, List<RemoteFileEntry> files, int partIndex, int partsTotal,
        long partOffset, long partLength)
    {
        PackageType = packageType;
        Files = files;
        PartIndex = partIndex;
        PartsTotal = partsTotal;
        PartOffset = partOffset;
        PartLength = partLength;
    }

    public EPackageType PackageType { get; }

    //Archive-სთვის — არქივის შემადგენელი ფაილები; WholeFile/FilePart-ისთვის — ერთი ფაილი
    public List<RemoteFileEntry> Files { get; }

    //მხოლოდ FilePart-ისთვის
    public int PartIndex { get; }
    public int PartsTotal { get; }
    public long PartOffset { get; }
    public long PartLength { get; }
}
