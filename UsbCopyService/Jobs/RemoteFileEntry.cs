namespace UsbCopyService.Jobs;

//წყაროდან შერჩეული ერთი ფაილის აღწერა
public sealed class RemoteFileEntry
{
    // ReSharper disable once ConvertToPrimaryConstructor
    public RemoteFileEntry(string? afterRootPath, string fileName, string wirePath, long fileLength)
    {
        AfterRootPath = afterRootPath;
        FileName = fileName;
        WirePath = wirePath;
        FileLength = fileLength;
    }

    //ფაილის საქაღალდის ფარდობითი გზა წყაროს გამყოფი სიმბოლოთი (null ნიშნავს ძირს)
    public string? AfterRootPath { get; }

    public string FileName { get; }

    //ფაილის სრული ფარდობითი გზა '/' გამყოფით — ამ სახით მიდის კლიენტთან
    public string WirePath { get; }

    public long FileLength { get; }
}
