using System.Collections.Generic;
using ParametersManagement.LibFileParameters.Models;

namespace UsbCopyService.Settings;

//სერვისის პარამეტრები, იკითხება appsettings.json-ის UsbCopySettings სექციიდან
public sealed class UsbCopySettings
{
    public const string SectionName = "UsbCopySettings";

    //სამუშაო საქაღალდე, სადაც დროებით ინახება ჩამოტვირთული ფაილები და შექმნილი არქივები
    public string? WorkPath { get; set; }

    //ამ ზომამდე (მეგაბაიტებში) ფაილები პატარად ითვლება და zip არქივებში ერთიანდება
    public int SmallFileMaxSizeMb { get; set; } = 4;

    //ერთი zip არქივის შემადგენელი ფაილების ჯამური საწყისი ზომის ზღვარი (მეგაბაიტებში)
    public int ArchiveVolumeMaxSizeMb { get; set; } = 200;

    //ამ ზომაზე (მეგაბაიტებში) დიდი ფაილები ნაწილებად იყოფა
    public int PartMaxSizeMb { get; set; } = 256;

    //რამდენ ხანს დაელოდოს სერვისი კლიენტის დასტურს ერთ პაკეტზე
    public int AckTimeoutMinutes { get; set; } = 60;

    //გაშვებისას სამუშაო საქაღალდეში დარჩენილი ძველი job_* ქვესაქაღალდეების წაშლა
    public bool CleanWorkPathOnStart { get; set; } = true;

    public Dictionary<string, FileStorageData> FileStorages { get; set; } = new();

    public Dictionary<string, ExcludeSet> ExcludeSets { get; set; } = new();

    public Dictionary<string, UsbCopyServiceProjectSettings> Projects { get; set; } = new();

    public long SmallFileMaxSizeBytes => SmallFileMaxSizeMb * 1024L * 1024L;
    public long ArchiveVolumeMaxSizeBytes => ArchiveVolumeMaxSizeMb * 1024L * 1024L;
    public long PartMaxSizeBytes => PartMaxSizeMb * 1024L * 1024L;
}
