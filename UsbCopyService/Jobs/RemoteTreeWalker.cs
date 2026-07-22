using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConnectionTools.ConnectTools;
using SystemTools.SystemToolsShared;
using ToolsManagement.FileManagersMain;

namespace UsbCopyService.Jobs;

//წყაროს (FTP ან დისკი) ხის რეკურსიული დათვალიერება UsbCopy-ს წესებით:
//გამორიცხვის მასკები, '#'/'@' საქაღალდეების გამოტოვება, თარიღიანი ჯგუფებიდან მხოლოდ უახლესის შერჩევა
public sealed class RemoteTreeWalker
{
    private const string TimestampMask = "yyyyMMddHHmmssfffffff";

    private readonly string[] _excludes;
    private readonly HashSet<string> _existingFiles;
    private readonly FileManager _fileManager;
    private readonly Func<string, Task> _progress;

    // ReSharper disable once ConvertToPrimaryConstructor
    public RemoteTreeWalker(FileManager fileManager, string[] excludes, HashSet<string> existingFiles,
        Func<string, Task> progress)
    {
        _fileManager = fileManager;
        _excludes = excludes;
        _existingFiles = existingFiles;
        _progress = progress;
    }

    public async Task<WalkResult> CollectAsync(CancellationToken cancellationToken = default)
    {
        List<RemoteFileEntry> files = [];
        var skippedExisting = 0;
        await ProcessFolder(files, null, () => skippedExisting++, cancellationToken);
        return new WalkResult(files, skippedExisting);
    }

    private async Task ProcessFolder(List<RemoteFileEntry> files, string? afterRootPath, Action onSkippedExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<string> folderNames = _fileManager.GetFolderNames(afterRootPath, null);
        foreach (string folderName in folderNames.OrderBy(o => o, StringComparer.Ordinal))
        {
            string folderAfterRootFullName = _fileManager.PathCombine(afterRootPath, folderName);
            if (NeedExclude(folderAfterRootFullName))
            {
                continue;
            }

            //სპეციალური სიმბოლოებიანი საქაღალდეები არ კოპირდება (როგორც UsbCopy-შია)
            if (folderName.Contains('#') || folderName.Contains('@'))
            {
                continue;
            }

            await _progress($"Scanning {folderAfterRootFullName}");
            await ProcessFolder(files, folderAfterRootFullName, onSkippedExisting, cancellationToken);
        }

        List<MyFileInfo> folderFiles = _fileManager.GetFilesWithInfo(afterRootPath, null)
            .Where(file => !NeedExclude(_fileManager.PathCombine(afterRootPath, file.FileName))).ToList();

        foreach (MyFileInfo fileInfo in SelectFiles(folderFiles))
        {
            string wirePath = ToWirePath(afterRootPath, fileInfo.FileName);
            if (_existingFiles.Contains(wirePath))
            {
                onSkippedExisting();
                continue;
            }

            files.Add(new RemoteFileEntry(afterRootPath, fileInfo.FileName, wirePath, fileInfo.FileLength));
        }
    }

    //თარიღიანი ბექაპ-ფაილების ჯგუფებიდან მხოლოდ უახლესი ფაილი შეირჩევა, დანარჩენები უცვლელად გადმოდის
    private static List<MyFileInfo> SelectFiles(List<MyFileInfo> folderFiles)
    {
        List<MyFileInfo> result = [];
        Dictionary<string, List<(MyFileInfo FileInfo, DateTime FileDateTime)>> fileByPatterns = new(StringComparer.Ordinal);

        foreach (MyFileInfo fileInfo in folderFiles)
        {
            (DateTime dateTimeByDigits, string? pattern) = fileInfo.FileName.GetDateTimeAndPatternByDigits(TimestampMask);

            if (pattern is null)
            {
                result.Add(fileInfo);
                continue;
            }

            if (!fileByPatterns.TryGetValue(pattern, out List<(MyFileInfo FileInfo, DateTime FileDateTime)>? value))
            {
                value = [];
                fileByPatterns.Add(pattern, value);
            }

            value.Add((fileInfo, dateTimeByDigits));
        }

        foreach (KeyValuePair<string, List<(MyFileInfo FileInfo, DateTime FileDateTime)>> kvp in
                 fileByPatterns.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            result.Add(kvp.Value.OrderByDescending(o => o.FileDateTime).First().FileInfo);
        }

        return result;
    }

    private bool NeedExclude(string name)
    {
        return _excludes.Length > 0 && _excludes.Any(name.FitsMask);
    }

    private string ToWirePath(string? afterRootPath, string fileName)
    {
        string full = _fileManager.PathCombine(afterRootPath, fileName);
        return full.Replace(_fileManager.DirectorySeparatorChar, '/');
    }
}
