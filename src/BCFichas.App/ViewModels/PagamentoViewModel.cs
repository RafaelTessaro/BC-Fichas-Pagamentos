using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Servicos;
using BCFichas.Core.Vendas;

namespace BCFichas.App.ViewModels;

public enum EtapaPagamento
{
    Escolher,
    Dinheiro,
    Maquininha,
    Concluido,
    Recusado,
    ErroImpressao,
}

/// <summary>
/// Janela de pagamento: escolhe a forma, recebe (dinheiro ou maquininha) e imprime as fichas.
/// </summary>
public sealed partial class PagamentoViewModel : ViewModelBase
{
    private readonly PrincipalViewModel _principal;
    private readonly SessaoCaixa _sessao;
    private readonly IReadOnlyList<LinhaCarrinho> _linhas;
    private readonly Action _aoConcluir;
    private CancellationTokenSource? _cancelar;
    private Pedido? _pedido;
    private FormaPagamento _forma;

    public PagamentoViewModel(PrincipalViewModel principal, SessaoCaixa sessao, IReadOnlyList<LinhaCarrinho> linhas,
        Action aoConcluir)
    {
        _principal = principal;
        _sessao = sessao;
        _linhas = linhas;
        _aoConcluir = aoConcluir;
        TotalCentavos = linhas.Sum(l => l.TotalCentavos);
        Recebido.PropertyChanged += (_, _) => AtualizarTroco();
        var fichas = linhas.Sum(l => l.Quantidade * l.Produto.FichasPorVenda);
        ResumoItens = $"{linhas.Sum(l => l.Quantidade)} item(ns) • {fichas} ficha(s)";
    }

    public long TotalCentavos { get; }
    public string Total => Dinheiro.Formatar(TotalCentavos);
    public string ResumoItens { get; }
    public EntradaValor Recebido { get; } = new();
    public bool EhSimulador => _principal.Sistema.Maquininha is MaquininhaSimulada;

    /// <summary>Maquininha usada à parte: o operador cobra nela e confirma aqui.</summary>
    public bool EhSeparada => _principal.Sistema.Maquininha is MaquininhaSeparada;

    /// <summary>Esperando a resposta de uma maquininha ligada ao programa (simulador, app ponte).</summary>
    public bool EhIntegrada => !EhSeparada;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmEscolher), nameof(EmDinheiro), nameof(EmMaquininha), nameof(EmConcluido),
        nameof(EmRecusado), nameof(EmErroImpressao), nameof(PodeFechar))]
    private EtapaPagamento _etapa = EtapaPagamento.Escolher;

    [ObservableProperty] private string _troco = Dinheiro.Formatar(0);
    [ObservableProperty] private bool _faltaDinheiro = true;
    [ObservableProperty] private string _andamento = "";
    [ObservableProperty] private string _mensagem = "";
    [ObservableProperty] private string _formaTexto = "";
    [ObservableProperty] private Avalonia.Media.Geometry? _iconeForma;
    /// <summary>Cor do ícone: o símbolo do Pix só pode sair no verde oficial dele (manual da marca do Banco Central).</summary>
    [ObservableProperty] private Avalonia.Media.IBrush? _corForma;
    [ObservableProperty] private Avalonia.Media.IBrush? _fundoForma;
    [ObservableProperty] private string _resultadoImpressao = "";
    [ObservableProperty] private bool _imprimindo;
    [ObservableProperty] private bool _temTroco;

    public bool EmEscolher => Etapa == EtapaPagamento.Escolher;
    public bool EmDinheiro => Etapa == EtapaPagamento.Dinheiro;
    public bool EmMaquininha => Etapa == EtapaPagamento.Maquininha;
    public bool EmConcluido => Etapa == EtapaPagamento.Concluido;
    public bool EmRecusado => Etapa == EtapaPagamento.Recusado;
    public bool EmErroImpressao => Etapa == EtapaPagamento.ErroImpressao;
    public bool PodeFechar => Etapa is EtapaPagamento.Escolher or EtapaPagamento.Dinheiro or EtapaPagamento.Recusado;

    [RelayCommand]
    private void EscolherDinheiro()
    {
        _forma = FormaPagamento.Dinheiro;
        FormaTexto = "Dinheiro";
        Recebido.Centavos = 0;
        AtualizarTroco();
        Etapa = EtapaPagamento.Dinheiro;
    }

    [RelayCommand]
    private void ValorExato() => Recebido.Centavos = TotalCentavos;

    [RelayCommand]
    private async Task ConfirmarDinheiro()
    {
        if (Recebido.Centavos < TotalCentavos)
        {
            _principal.MostrarAviso("O valor recebido é menor que o total.", erro: true);
            return;
        }
        try
        {
            _pedido = _principal.Sistema.Vendas.CriarPedido(_sessao, _linhas, FormaPagamento.Dinheiro, Recebido.Centavos);
        }
        catch (ErroDeNegocio e)
        {
            Mensagem = e.Message;
            Etapa = EtapaPagamento.Recusado;
            return;
        }
        await Concluir();
    }

    [RelayCommand]
    private Task Debito() => Maquininha(FormaPagamento.Debito);

    [RelayCommand]
    private Task Credito() => Maquininha(FormaPagamento.Credito);

    [RelayCommand]
    private Task Pix() => Maquininha(FormaPagamento.Pix);

    private async Task Maquininha(FormaPagamento forma)
    {
        _forma = forma;
        FormaTexto = Nomes.De(forma);
        IconeForma = Recursos.Icone(forma switch
        {
            FormaPagamento.Pix => "IconePix",
            FormaPagamento.Credito => "IconeCartaoChip",
            _ => "IconeCartao",
        });
        CorForma = Recursos.Pincel(forma == FormaPagamento.Pix ? "PixVerde" : "Primaria");
        FundoForma = Recursos.Pincel(forma == FormaPagamento.Pix ? "GradientePix" : "PrimariaClara");
        Andamento = "Enviando para a maquininha...";
        Etapa = EtapaPagamento.Maquininha;

        try
        {
            _pedido = _principal.Sistema.Vendas.CriarPedido(_sessao, _linhas, forma);
        }
        catch (ErroDeNegocio e)
        {
            Mensagem = e.Message;
            Etapa = EtapaPagamento.Recusado;
            return;
        }

        _cancelar = new CancellationTokenSource();
        var andamento = new Progress<string>(texto => Andamento = texto);
        ResultadoCobranca resultado;
        try
        {
            resultado = await _principal.Sistema.Maquininha.CobrarAsync(
                new Cobranca(VendaServico.IdCobranca(_pedido), _pedido.TotalCentavos, forma,
                    $"{_principal.Config.NomeEvento} - pedido {_pedido.Numero}"),
                andamento, _cancelar.Token);
        }
        catch (OperationCanceledException)
        {
            _principal.Sistema.Vendas.Cancelar(_pedido.Id);
            _pedido = null;
            Etapa = EtapaPagamento.Escolher;
            return;
        }
        catch (Exception e)
        {
            Log.Erro("Maquininha", e);
            _principal.Sistema.Vendas.Cancelar(_pedido.Id);
            _pedido = null;
            Mensagem = "Não consegui falar com a maquininha: " + e.Message;
            Etapa = EtapaPagamento.Recusado;
            return;
        }
        finally
        {
            _cancelar?.Dispose();
            _cancelar = null;
        }

        if (!resultado.Aprovado)
        {
            _principal.Sistema.Vendas.Cancelar(_pedido.Id);
            _pedido = null;
            Mensagem = resultado.Mensagem;
            Etapa = EtapaPagamento.Recusado;
            return;
        }

        _pedido = _principal.Sistema.Vendas.ConfirmarPagamento(_pedido.Id, resultado.Autorizacao);
        await Concluir();
    }

    [RelayCommand]
    private void CancelarMaquininha() => _cancelar?.Cancel();

    [RelayCommand]
    private void SimularAprovar() => (_principal.Sistema.Maquininha as MaquininhaSimulada)?.Aprovar();

    [RelayCommand]
    private void SimularRecusar() => (_principal.Sistema.Maquininha as MaquininhaSimulada)?.Recusar("Cartão recusado (simulação)");

    /// <summary>A maquininha separada aprovou: o operador confirma e as fichas saem.</summary>
    [RelayCommand]
    private void ConfirmarNaMaquininha() => (_principal.Sistema.Maquininha as MaquininhaSeparada)?.Aprovar();

    [RelayCommand]
    private void NaoAprovou() =>
        (_principal.Sistema.Maquininha as MaquininhaSeparada)?.Recusar("A maquininha não aprovou o pagamento.");

    [RelayCommand]
    private void OutraForma()
    {
        Mensagem = "";
        Etapa = EtapaPagamento.Escolher;
    }

    [RelayCommand]
    private Task TentarImprimir() => Imprimir();

    [RelayCommand]
    private void Fechar()
    {
        if (Etapa == EtapaPagamento.Maquininha) return;
        _principal.FecharDialogo(this);
    }

    /// <summary>Pago: limpa o pedido da tela e imprime as fichas.</summary>
    private async Task Concluir()
    {
        _aoConcluir();
        TemTroco = _pedido!.TrocoCentavos > 0;
        Troco = Dinheiro.Formatar(_pedido.TrocoCentavos);
        await Imprimir();
    }

    private async Task Imprimir()
    {
        if (_pedido is null) return;
        Imprimindo = true;
        ResultadoImpressao = $"Imprimindo {_pedido.QuantidadeFichas} ficha(s)...";
        Etapa = EtapaPagamento.Concluido;

        var pedido = _pedido;
        var fichas = GeradorFichas.Gerar(pedido, _principal.Config);
        var resultado = await Task.Run(() => _principal.Sistema.Impressao.Fichas(fichas));
        Imprimindo = false;

        if (resultado.Ok)
        {
            _principal.Sistema.Vendas.RegistrarImpressao(pedido.Id);
            ResultadoImpressao = resultado.Mensagem;
            if (!TemTroco) _ = FecharSozinho();
        }
        else
        {
            Mensagem = resultado.Mensagem;
            Etapa = EtapaPagamento.ErroImpressao;
        }
    }

    private async Task FecharSozinho()
    {
        await Task.Delay(2500);
        if (Etapa == EtapaPagamento.Concluido) _principal.FecharDialogo(this);
    }

    private void AtualizarTroco()
    {
        var troco = Recebido.Centavos - TotalCentavos;
        FaltaDinheiro = troco < 0;
        Troco = troco < 0 ? "Falta " + Dinheiro.Formatar(-troco) : Dinheiro.Formatar(troco);
    }
}
