using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Pagamento;

namespace BCFichas.App.ViewModels;

/// <summary>Um aparelho Bluetooth pareado com o tablet, na lista da aba Maquininha.</summary>
public sealed record AparelhoOpcao(string Nome, string Ligacao, bool Conectado)
{
    public string Detalhe => Ligacao + (Conectado ? " • conectado" : "");
    public override string ToString() => Nome;
}

/// <summary>Aba Maquininha: a Moderninha Smart 2 do PagBank ligada por Bluetooth.</summary>
public sealed partial class ConfiguracaoViewModel
{
    private const int AbaMaquininha = 4;

    /// <summary>Aparelhos pareados com o tablet (os testes trocam: no computador dos testes não há Bluetooth).</summary>
    public static Func<List<AparelhoBluetooth>> ListarAparelhos { get; set; } = BluetoothWindows.Pareados;

    public ObservableCollection<AparelhoOpcao> Aparelhos { get; } = new();

    /// <summary>Endereço Bluetooth da maquininha (ou porta COM): o que vai para a configuração.</summary>
    [ObservableProperty] private string _maquininhaLigacao = "";
    [ObservableProperty] private string _maquininhaNome = "";
    [ObservableProperty] private bool _maquininhaComprovante;
    [ObservableProperty] private AparelhoOpcao? _aparelhoEscolhido;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestarMaquininhaCommand))]
    private bool _testandoMaquininha;

    [ObservableProperty] private string _resultadoTeste = "";
    [ObservableProperty] private bool _testeOk;
    [ObservableProperty] private bool _testeFalhou;

    public bool MaquininhaSmart => TipoMaquininha?.Valor == Core.TipoMaquininha.PagBankSmart;
    public bool SemAparelhos => Aparelhos.Count == 0;

    /// <summary>Escolheu na lista: a ligação passa a ser a desse aparelho.</summary>
    partial void OnAparelhoEscolhidoChanged(AparelhoOpcao? value)
    {
        if (value is null) return;
        MaquininhaLigacao = value.Ligacao;
        MaquininhaNome = value.Nome;
    }

    partial void OnMaquininhaLigacaoChanged(string value)
    {
        // Digitou outra coisa: o teste anterior não vale mais
        ResultadoTeste = "";
        TesteOk = TesteFalhou = false;
        if (AparelhoEscolhido is { } a && !Mesma(a.Ligacao, value)) AparelhoEscolhido = null;
    }

    /// <summary>Lista de novo os aparelhos pareados (depois de parear a maquininha no Windows).</summary>
    [RelayCommand]
    private void ProcurarAparelhos()
    {
        List<AparelhoBluetooth> pareados;
        try
        {
            pareados = ListarAparelhos();
        }
        catch (Exception e)
        {
            Log.Erro("Listar os aparelhos Bluetooth", e);
            pareados = [];
        }
        var ligacao = MaquininhaLigacao;
        Aparelhos.Clear();
        // As maquininhas primeiro (o nome do Bluetooth delas costuma ter "PagBank", "Moderninha" ou o modelo)
        foreach (var a in pareados.OrderBy(a => PareceMaquininha(a.Nome) ? 0 : 1).ThenBy(a => a.Nome, StringComparer.OrdinalIgnoreCase))
            Aparelhos.Add(new AparelhoOpcao(a.Nome, a.EnderecoTexto, a.Conectado));
        AparelhoEscolhido = Aparelhos.FirstOrDefault(a => Mesma(a.Ligacao, ligacao));
        MaquininhaLigacao = ligacao;
        OnPropertyChanged(nameof(SemAparelhos));
    }

    private static bool PareceMaquininha(string nome) =>
        new[] { "pagbank", "pagseguro", "moderninha", "smart", "p2", "gpos" }
            .Any(p => nome.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static bool Mesma(string a, string b) =>
        BluetoothWindows.TentarLer(a, out var x) && BluetoothWindows.TentarLer(b, out var y)
            ? x == y
            : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool PodeTestar => !TestandoMaquininha;

    /// <summary>Liga na maquininha escolhida (mesmo antes de salvar) e mostra o que o app dela respondeu.</summary>
    [RelayCommand(CanExecute = nameof(PodeTestar))]
    private async Task TestarMaquininha()
    {
        TesteOk = TesteFalhou = false;
        var conexao = ConexaoPonte.Criar(MaquininhaLigacao);
        if (conexao is null)
        {
            TesteFalhou = true;
            ResultadoTeste = "Escolha a maquininha na lista (ou digite o endereço Bluetooth ou a porta COM).";
            return;
        }
        TestandoMaquininha = true;
        ResultadoTeste = $"Ligando na maquininha ({conexao.Descricao})...";
        try
        {
            using var maquininha = new MaquininhaPagBank(conexao, NumeroCaixa, MaquininhaComprovante);
            var info = await maquininha.TestarAsync(CancellationToken.None);
            var serial = info.Serial.Length > 0 ? $" (nº de série {info.Serial})" : "";
            TesteOk = info.Pronta;
            TesteFalhou = !info.Pronta;
            ResultadoTeste = info.Pronta
                ? $"Ligou! {info.Modelo}{serial} pronta para cobrar."
                : $"Ligou na {info.Modelo}{serial}, mas ela não está pronta: " +
                  (info.Mensagem.Length > 0 ? info.Mensagem : "confira se ela está ativada no PagBank.");
        }
        catch (ErroDeNegocio e)
        {
            TesteFalhou = true;
            ResultadoTeste = e.Message;
        }
        catch (Exception e)
        {
            Log.Erro("Testar a maquininha", e);
            TesteFalhou = true;
            ResultadoTeste = "Não consegui ligar na maquininha: " + e.Message;
        }
        finally
        {
            TestandoMaquininha = false;
        }
    }

    /// <summary>Smart 2 escolhida sem saber onde ela está: não deixa salvar (toda venda em cartão daria erro).</summary>
    private bool MaquininhaValida()
    {
        if (!MaquininhaSmart || ConexaoPonte.Criar(MaquininhaLigacao) is not null) return true;
        AbaSelecionada = AbaMaquininha;
        Principal.MostrarAviso("Escolha a Moderninha Smart 2 na lista da aba Maquininha.", erro: true);
        return false;
    }

    private void CarregarMaquininha(Configuracao c)
    {
        MaquininhaComprovante = c.MaquininhaComprovante;
        MaquininhaNome = c.MaquininhaNome;
        MaquininhaLigacao = c.MaquininhaLigacao;
        ProcurarAparelhos();
        // A maquininha escolhida antes pode não estar mais pareada: continua escolhida (pelo endereço)
        MaquininhaLigacao = c.MaquininhaLigacao;
    }

    private void MontarMaquininha(Configuracao c)
    {
        c.MaquininhaLigacao = MaquininhaLigacao.Trim();
        c.MaquininhaNome = (AparelhoEscolhido?.Nome ?? MaquininhaNome).Trim();
        c.MaquininhaComprovante = MaquininhaComprovante;
    }
}
