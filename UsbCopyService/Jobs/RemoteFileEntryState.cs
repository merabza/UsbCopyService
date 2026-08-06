namespace UsbCopyService.Jobs;

//RemoteFileEntry-ის შესანახი ასლი state.json-ისთვის
public sealed class RemoteFileEntryState
{
    public string? AfterRootPath { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string WirePath { get; set; } = string.Empty;

    public long FileLength { get; set; }

    public static RemoteFileEntryState From(RemoteFileEntry entry)
    {
        return new RemoteFileEntryState
        {
            AfterRootPath = entry.AfterRootPath,
            FileName = entry.FileName,
            WirePath = entry.WirePath,
            FileLength = entry.FileLength
        };
    }

    public RemoteFileEntry ToRemoteFileEntry()
    {
        return new RemoteFileEntry(AfterRootPath, FileName, WirePath, FileLength);
    }
}
