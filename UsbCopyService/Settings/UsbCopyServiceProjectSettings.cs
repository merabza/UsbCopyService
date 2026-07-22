namespace UsbCopyService.Settings;

//ერთი პროექტის აღწერა სერვისის მხარეს: საიდან უნდა წამოვიდეს ფაილები და რა უნდა გამოირიცხოს
public sealed class UsbCopyServiceProjectSettings
{
    public string? FileStorageName { get; set; }
    public string? ExcludeSetName { get; set; }
}
