using System.Text.Json;

namespace CustomsClearanceConsole;

internal sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string StatePath;
    public StateStore(string? path = null) => StatePath = path ?? Path.Combine(AppLog.Folder, "history.json");

    public AppState Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return new AppState();
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), JsonOptions) ?? new AppState();
            // Older duplicate analysis overwrote recognition status. Never infer that
            // these historical records were validated successfully.
            foreach (var record in state.Records.Where(x => x.Status == "重复单号"))
            {
                record.Status = "需关注";
                record.Warning = string.Join("；", new[] { record.Warning, "历史重复记录的识别状态不完整，请重新识别源文件" }.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            state.UiSchemaVersion = AppState.CurrentUiSchemaVersion;
            BatchScanner.MarkDuplicates(state.Records);
            return state;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(StatePath))!);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, StatePath, true);
    }

}
