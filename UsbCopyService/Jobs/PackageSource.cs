namespace UsbCopyService.Jobs;

//ინფორმაცია, საიდან უნდა წაიკითხოს download ენდპოინტმა მიმდინარე პაკეტის ბაიტები
public sealed class PackageSource
{
    // ReSharper disable once ConvertToPrimaryConstructor
    public PackageSource(string packageId, string filePath, long offset, long length)
    {
        PackageId = packageId;
        FilePath = filePath;
        Offset = offset;
        Length = length;
    }

    public string PackageId { get; }
    public string FilePath { get; }
    public long Offset { get; }
    public long Length { get; }
}
