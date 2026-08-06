using System.Text.Json.Serialization;

namespace UsbCopyService.Jobs;

//სამუშაოს ფაზა state.json-ში: განსაზღვრავს, საიდან უნდა გაგრძელდეს აღდგენილი სამუშაო
[JsonConverter(typeof(JsonStringEnumConverter<EJobPhase>))]
public enum EJobPhase
{
    //წყაროს დათვალიერება და ფაილების ჩამოტვირთვა ჯერ არ დასრულებულა — აღდგენისას თავიდან სრულდება
    Staging,

    //გეგმა აგებულია და პაკეტები მიეწოდება — აღდგენისას გაგრძელება NextPackageIndex-დან
    Transferring
}
