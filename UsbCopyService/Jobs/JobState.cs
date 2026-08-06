using System;
using System.Collections.Generic;

namespace UsbCopyService.Jobs;

//სამუშაოს დისკზე შენახული მდგომარეობა ({workDir}\state.json) — სერვისის გადატვირთვის შემდეგ აღდგენისთვის
public sealed class JobState
{
    public int Version { get; set; } = 1;

    public string JobId { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    //კლიენტის მიერ StartJob-ზე გადმოცემული უკვე არსებული ფაილების სია
    public string[] ExistingFiles { get; set; } = [];

    public EJobPhase Phase { get; set; }

    //პაკეტების გეგმა; null სანამ Staging ფაზა არ დასრულდება
    public List<PlannedPackageState>? Plan { get; set; }

    //შემდეგი მისაწოდებელი პაკეტის ინდექსი (ყველა უფრო ადრეული უკვე დადასტურებულია)
    public int NextPackageIndex { get; set; }

    public int PackagesTotal { get; set; }

    public int FilesTotal { get; set; }

    public int FilesSkipped { get; set; }

    public long BytesOriginal { get; set; }

    public long BytesTransferred { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
