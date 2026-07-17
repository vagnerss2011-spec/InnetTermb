namespace RemoteOps.Desktop.ViewModels;

public sealed class GroupCardViewModel : BaseViewModel
{
    private string _name;
    private int _hostCount;

    public GroupCardViewModel(string id, string name, int hostCount)
    {
        Id = id;
        _name = name;
        _hostCount = hostCount;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public int HostCount
    {
        get => _hostCount;
        private set
        {
            Set(ref _hostCount, value);
            // HostCountLabel deriva de HostCount — precisa notificar junto senão o card mostra a
            // contagem velha quando o sync adiciona/remove um host no grupo aberto.
            RaisePropertyChanged(nameof(HostCountLabel));
        }
    }

    public string HostCountLabel => HostCount == 1 ? "1 host" : $"{HostCount} hosts";

    /// <summary>
    /// Atualiza nome/contagem in-place (reconciliação do sync, Fase 2) preservando a INSTÂNCIA do
    /// card — recriar o card quebraria a referência de <c>CurrentGroup</c> e o binding aberto.
    /// </summary>
    public void Update(string name, int hostCount)
    {
        Name = name;
        HostCount = hostCount;
    }
}
