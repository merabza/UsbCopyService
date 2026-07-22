using System.Threading.Tasks;

namespace UsbCopyService.Jobs;

//მიმდინარე (კლიენტისთვის შეთავაზებული) პაკეტის მდგომარეობა
public sealed class PackageCurrent
{
    // ReSharper disable once ConvertToPrimaryConstructor
    public PackageCurrent(PackageSource source, TaskCompletionSource<AckResult> ackTcs)
    {
        Source = source;
        AckTcs = ackTcs;
    }

    public PackageSource Source { get; }
    public TaskCompletionSource<AckResult> AckTcs { get; }
}
