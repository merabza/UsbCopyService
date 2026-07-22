using System.Collections.Generic;

namespace UsbCopyService.Jobs;

//წყაროს ხის დათვალიერების შედეგი
public sealed class WalkResult
{
    // ReSharper disable once ConvertToPrimaryConstructor
    public WalkResult(List<RemoteFileEntry> files, int skippedExisting)
    {
        Files = files;
        SkippedExisting = skippedExisting;
    }

    public List<RemoteFileEntry> Files { get; }

    //კლიენტთან უკვე არსებობის გამო გამოტოვებული ფაილების რაოდენობა
    public int SkippedExisting { get; }
}
