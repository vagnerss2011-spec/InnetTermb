using System.Globalization;

using RemoteOps.Desktop.Account;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// ⚠️ <b>O aviso da fila parada no cofre que esta sessão NÃO abriu.</b>
///
/// <para><b>Por que ele existe:</b> desde o 1j há um banco por escopo, e o outbox mora no banco. O
/// sync de uma sessão drena o banco DAQUELA sessão e mais nenhum. Então o operador edita um cliente
/// no cofre pessoal, fecha, abre no time — e aquelas edições ficam esperando, enquanto a barra diz
/// "Sincronizado" com toda a razão (o ciclo desta sessão realmente terminou). Ele conclui que subiu.
/// É a queixa que ele já abriu duas vezes ("as credenciais não sincronizaram"), e é a classe de
/// defeito nº 1 desta base: trabalho que fica para trás sem uma linha de erro.</para>
///
/// <para><b>O aviso precisa das duas metades.</b> Só o número ("12 alterações paradas") vira mais um
/// enfeite inexplicável na barra; só o conselho, sem número, não convence ninguém a fechar o app no
/// meio do expediente. Daí <see cref="Text"/> (quantos) e <see cref="Detail"/> (o que fazer).</para>
///
/// <para><b>E "não deu para conferir" APARECE.</b> Um cofre que não pôde ser lido nunca vira "está
/// tudo sincronizado" — é exatamente assim que erro vira estado vazio aqui.</para>
/// </summary>
public sealed class OtherVaultOutboxViewModel : BaseViewModel
{
    private int _personal;
    private int _team;
    private bool _checkFailed;

    /// <summary>
    /// Tem o que dizer? Falso é o caso da maioria da frota (um cofre só) — e aviso permanente é
    /// aviso que ninguém lê.
    /// </summary>
    public bool HasNotice => Total > 0 || _checkFailed;

    /// <summary>O que cabe na barra: quantos itens, e em qual cofre.</summary>
    public string Text
    {
        get
        {
            if (Total == 0)
            {
                return _checkFailed
                    // Sem número, porque não há número medido. Afirmar "0" seria dizer "está tudo
                    // certo lá" sobre um cofre que o app não conseguiu abrir.
                    ? "Fila do outro cofre não verificada"
                    : string.Empty;
            }

            string quantidade = Total.ToString(CultureInfo.GetCultureInfo("pt-BR"));
            return Total == 1
                ? $"1 alteração parada {Onde}"
                : $"{quantidade} alterações paradas {Onde}";
        }
    }

    /// <summary>
    /// A frase inteira (tooltip): o que aconteceu, o que fazer e — o que o operador mais precisa
    /// ouvir — que nada foi perdido. Sem essa última parte, o aviso parece anúncio de estrago e o
    /// primeiro reflexo é refazer o cadastro, criando duplicata.
    ///
    /// <para><b>O "como abrir o outro cofre" sai da fonte única (<see cref="VaultSwitchText"/>).</b>
    /// A versão anterior mandava <i>"feche o RemoteOps e abra de novo escolhendo o cofre"</i> — a
    /// MESMA mentira das quatro telas do H2, nascida aqui no G3, antes de o botão existir: com o
    /// cache da AMK em disco, reabrir entra direto no MESMO cofre e não pergunta nada. O operador
    /// seguia o conselho, caía onde já estava, e a fila continuava parada — justamente a falha que
    /// este aviso existe para matar. Quem troca de cofre é o botão, e a frase agora o nomeia.</para>
    /// </summary>
    public string Detail
    {
        get
        {
            if (Total == 0)
            {
                return _checkFailed
                    ? "Não foi possível ler a fila de sincronização do outro cofre neste computador "
                        + "agora. Pode haver alteração esperando para subir lá — para abrir aquele "
                        + "cofre e conferir, " + VaultSwitchText.HowToSwitch
                    : string.Empty;
            }

            string frase =
                $"Essas alterações foram feitas {Onde} e continuam guardadas na fila dele. Elas só "
                + $"sobem quando você abrir o RemoteOps {NoQual} — para isso, "
                + $"{VaultSwitchText.HowToSwitch} Nada foi perdido.";

            return _checkFailed
                ? frase + " Um dos cofres não pôde ser lido agora, então pode haver mais coisa "
                    + "esperando do que este número mostra."
                : frase;
        }
    }

    /// <summary>Reflete o resultado da sondagem do boot. Ver <c>OtherVaultOutboxProbe</c>.</summary>
    public void Apply(int pendingPersonal, int pendingTeam, bool checkFailed)
    {
        _personal = pendingPersonal;
        _team = pendingTeam;
        _checkFailed = checkFailed;

        // Todas as propriedades daqui são DERIVADAS dos três campos acima: sem os raises explícitos
        // o aviso nasceria certo e envelheceria errado na tela, que é o pior dos dois mundos (mesma
        // lição do VaultBadgeViewModel).
        RaisePropertyChanged(nameof(HasNotice));
        RaisePropertyChanged(nameof(Text));
        RaisePropertyChanged(nameof(Detail));
    }

    private int Total => _personal + _team;

    /// <summary>Com preposição, para a frase sair em português de gente nos três casos.</summary>
    private string Onde => (_personal > 0, _team > 0) switch
    {
        (true, true) => "nos outros cofres",
        (false, true) => "no cofre do time",
        _ => "no cofre pessoal",
    };

    /// <summary>Idem, para o "abrir o RemoteOps {aqui}" — no plural, um de cada vez.</summary>
    private string NoQual => (_personal > 0, _team > 0) switch
    {
        (true, true) => "em cada um deles",
        (false, true) => "no cofre do time",
        _ => "no cofre pessoal",
    };
}
