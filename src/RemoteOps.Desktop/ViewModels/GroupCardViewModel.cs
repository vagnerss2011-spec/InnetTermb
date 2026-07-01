namespace RemoteOps.Desktop.ViewModels;

public sealed class GroupCardViewModel
{
    public GroupCardViewModel(string id, string name, int hostCount)
    {
        Id = id;
        Name = name;
        HostCount = hostCount;
    }

    public string Id { get; }
    public string Name { get; }
    public int HostCount { get; }
    public string HostCountLabel => HostCount == 1 ? "1 host" : $"{HostCount} hosts";
}
