namespace RemoteOps.Desktop.ViewModels;

public class SessionTabViewModel : BaseViewModel
{
    private string _title;
    private bool _isPinned;

    public SessionTabViewModel(string id, string title, string protocol)
    {
        Id = id;
        _title = title;
        Protocol = protocol;
    }

    public string Id { get; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string Protocol { get; }

    public bool IsPinned
    {
        get => _isPinned;
        set => Set(ref _isPinned, value);
    }

    public string ProtocolLabel => Protocol.ToUpperInvariant();
}
