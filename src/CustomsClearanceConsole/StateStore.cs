using System.Text.Json;

namespace CustomsClearanceConsole;

internal sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private string StatePath => Path.Combine(AppLog.Folder, "history.json");

    public AppState Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return new AppState();
            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), JsonOptions) ?? new AppState();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(AppLog.Folder);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, StatePath, true);
    }

    public void Clear()
    {
        if (File.Exists(StatePath)) File.Delete(StatePath);
    }
}
