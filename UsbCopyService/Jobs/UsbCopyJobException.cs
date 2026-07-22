using System;

namespace UsbCopyService.Jobs;

//სამუშაოს შეწყვეტის მიზეზის გადასაცემი გამონაკლისი
public sealed class UsbCopyJobException : Exception
{
    public UsbCopyJobException()
    {
    }

    // ReSharper disable once ConvertToPrimaryConstructor
    public UsbCopyJobException(string message) : base(message)
    {
    }

    public UsbCopyJobException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
