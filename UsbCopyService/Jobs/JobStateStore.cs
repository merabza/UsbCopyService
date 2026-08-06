using System;
using System.IO;
using System.Text.Json;

namespace UsbCopyService.Jobs;

//სამუშაოს მდგომარეობის ატომური შენახვა/წაკითხვა სამუშაო საქაღალდეში
public static class JobStateStore
{
    public const string StateFileName = "state.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    //ერთადერთი მწერალი სამუშაოს საკუთარი ლუპია, ამიტომ სინქრონიზაცია საჭირო არ არის
    public static void Save(string workDir, JobState state)
    {
        state.UpdatedAtUtc = DateTime.UtcNow;

        string statePath = Path.Combine(workDir, StateFileName);
        string tempPath = statePath + ".tmp";

        // ReSharper disable once using
        // ReSharper disable once DisposableConstructor
        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fileStream, state, SerializerOptions);
            fileStream.Flush(true);
        }

        File.Move(tempPath, statePath, true);
    }

    public static JobState? TryLoad(string workDir)
    {
        string statePath = Path.Combine(workDir, StateFileName);

        try
        {
            //წყვეტისას დარჩენილი დროებითი ფაილი უბრალოდ ნაგავია
            File.Delete(statePath + ".tmp");

            if (!File.Exists(statePath))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<JobState>(File.ReadAllText(statePath));
            return state?.Version == 1 ? state : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
