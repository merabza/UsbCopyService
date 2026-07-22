namespace UsbCopyService.Jobs;

//კლიენტის პასუხი ერთ პაკეტზე
public sealed class AckResult
{
    // ReSharper disable once ConvertToPrimaryConstructor
    public AckResult(bool ok, string? errorMessage)
    {
        Ok = ok;
        ErrorMessage = errorMessage;
    }

    public bool Ok { get; }
    public string? ErrorMessage { get; }
}
