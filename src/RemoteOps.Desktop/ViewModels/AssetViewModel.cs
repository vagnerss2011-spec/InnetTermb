using RemoteOps.Contracts.Assets;

namespace RemoteOps.Desktop.ViewModels;

public sealed class AssetViewModel : BaseViewModel
{
    private bool _isSelected;

    public AssetViewModel(Asset asset)
    {
        Asset = asset;
    }

    public Asset Asset { get; private set; }

    public string Id => Asset.Id;
    public string Name => Asset.Name;
    public string? Vendor => Asset.Vendor;
    public string? Model => Asset.Model;
    public string? Site => Asset.Site;
    public string Tags => string.Join(", ", Asset.Tags);

    public string PrimaryProtocol => Asset.Endpoints.Count > 0
        ? Asset.Endpoints[0].Protocol
        : string.Empty;

    public string PrimaryAddress => Asset.Endpoints.Count > 0
        ? (Asset.Endpoints[0].Fqdn ?? Asset.Endpoints[0].Ipv4 ?? Asset.Endpoints[0].Ipv6 ?? string.Empty)
        : string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public void Refresh(Asset updated)
    {
        Asset = updated;
        RaisePropertyChanged(nameof(Name));
        RaisePropertyChanged(nameof(Vendor));
        RaisePropertyChanged(nameof(Model));
        RaisePropertyChanged(nameof(PrimaryProtocol));
        RaisePropertyChanged(nameof(PrimaryAddress));
        RaisePropertyChanged(nameof(Tags));
    }
}
