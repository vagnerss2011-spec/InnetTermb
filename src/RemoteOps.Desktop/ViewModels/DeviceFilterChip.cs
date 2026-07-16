namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// Chip de filtro por tipo/vendor acima da lista de hosts. <see cref="Key"/> é vazio para "Todos",
/// <c>role:&lt;papel&gt;</c> para filtro por papel, ou <c>vendor:&lt;chave&gt;</c> para filtro por
/// vendor. Seleção única (o VM garante um ativo por vez).
/// </summary>
public sealed class DeviceFilterChip : BaseViewModel
{
    private bool _isActive;

    public DeviceFilterChip(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }
}
