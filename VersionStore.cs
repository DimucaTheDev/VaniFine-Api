namespace VaniFine.WebApi;

public class VersionStore
{
    public Dictionary<string, int> Versions { get; private set; } = new();
    public bool IsReady { get; private set; } = false;

    public void Update(Dictionary<string, int> newData)
    {
        Versions = newData;
        IsReady = true;
    }
}