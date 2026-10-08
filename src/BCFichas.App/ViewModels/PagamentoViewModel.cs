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
    /// <summary>A maquininha aprovou, mas o pedido não foi gravado como pago (erro no disco): tentar de novo.</summary>
    ErroGravacao,
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
    private bool _fechada;
    private DateTime? _cobrandoDesde;

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
        nameof(EmRecusado), nameof(EmErroImpressao), nameof(EmErroGravacao), nameof(PodeFechar))]
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
    public bool EmErroGravacao => Etapa == EtapaPagamento.ErroGravacao;
    public bool PodeFechar => !Gravando && Etapa is EtapaPagamento.Escolher or EtapaPagamento.Dinheiro or EtapaPagamento.Recusado;

    /// <summary>
    /// Gravando a venda em dinheiro (no tablet o disco é lento). Até terminar, Voltar, o X e as outras formas não
    /// fazem nada: um PIX escolhido nessa hora virava um segundo pedido, esperando a maquininha para sempre (e o
    /// caixa não fechava mais).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodeFechar))]
    private bool _gravando;

    /// <summary>
    /// A impressora falhou: dá para fechar e imprimir depois pelo menu (Reimprimir fichas ou, sem a reimpressão
    /// liberada, Fichas não impressas).
    /// </summary>
    public string TextoFecharSemImprimir => _principal.Config.LiberarReimpressao ? "Reimprimir depois" : "Imprimir depois";

    [RelayCommand]
    private void EscolherDinheiro()
    {
        if (Gravando) return;
        _forma = FormaPagamento.Dinheiro;
        FormaTexto = "Dinheiro";
        // Já vem com o valor da venda (pagou certinho: é só confirmar). Se o cliente deu mais, a primeira tecla
        // apaga e o operador digita o que recebeu.
        Recebido.Sugerir(TotalCentavos);
        AtualizarTroco();
        Etapa = EtapaPagamento.Dinheiro;
    }

    [RelayCommand]
    private async Task ConfirmarDinheiro()
    {
        if (Recebido.Centavos < TotalCentavos)
        {
            _principal.MostrarAviso("O valor recebido é menor que o total.", erro: true);
            return;
        }
        Gravando = true;
        try
        {
            // Gravar no banco fora da tela: no tablet o disco é lento e a tela não pode travar
            var recebido = Recebido.Centavos;
            _pedido = await Task.Run(() =>
                _principal.Sistema.Vendas.CriarPedido(_sessao, _linhas, FormaPagamento.Dinheiro, recebido));
        }
        catch (ErroDeNegocio e)
        {
            Mensagem = e.Message;
            Etapa = EtapaPagamento.Recusado;
            return;
        }
        finally
        {
            Gravando = false;
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
        if (Gravando) return;
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
            _pedido = await Task.Run(() => _principal.Sistema.Vendas.CriarPedido(_sessao, _linhas, forma));
        }
        catch (Exception e)
        {
            // Erro no disco também: nada foi para a maquininha ainda, então dá para escolher de novo (a janela não
            // pode ficar presa em "Enviando para a maquininha...")
            if (e is not ErroDeNegocio) Log.Erro("Gravar pedido", e);
            Mensagem = e is ErroDeNegocio ? e.Message : "Não consegui gravar o pedido: " + e.Message;
            Etapa = EtapaPagamento.Recusado;
            return;
        }

        _cancelar = new CancellationTokenSource();
        var andamento = new Progress<string>(texto => Andamento = texto);
        _cobrandoDesde = DateTime.UtcNow;
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
            CancelarPedido(_pedido.Id);
            _pedido = null;
            Etapa = EtapaPagamento.Escolher;
            return;
        }
        catch (Exception e)
        {
            Log.Erro("Maquininha", e);
            CancelarPedido(_pedido.Id);
            _pedido = null;
            Mensagem = "Não consegui falar com a maquininha: " + e.Message;
            Etapa = EtapaPagamento.Recusado;
            return;
        }
        finally
        {
            _cancelar?.Dispose();
            _cancelar = null;
            _cobrandoDesde = null;
        }

        if (!resultado.Aprovado)
        {
            CancelarPedido(_pedido.Id);
            _pedido = null;
            Mensagem = resultado.Mensagem;
            Etapa = EtapaPagamento.Recusado;
            return;
        }

        _autorizacao = resultado.Autorizacao;
        await GravarPago();
    }

    private string? _autorizacao;
    private bool _gravandoPago;

    /// <summary>
    /// A maquininha aprovou: grava o pedido como pago e as fichas saem. Se o disco falhar aqui, o cliente já pagou:
    /// o pedido não é cancelado nem volta para escolher a forma (seria cobrar de novo). Fica "Tentar de novo"; se o
    /// programa for fechado, ao abrir ele pergunta deste pedido.
    /// </summary>
    private async Task GravarPago()
    {
        if (_pedido is null || _gravandoPago) return;
        _gravandoPago = true;
        var pedidoId = _pedido.Id;
        try
        {
            _pedido = await Task.Run(() => _principal.Sistema.Vendas.ConfirmarPagamento(pedidoId, _autorizacao));
        }
        catch (Exception e)
        {
            Log.Erro("Confirmar pagamento", e);
            Mensagem = "A maquininha aprovou, mas não consegui gravar a venda: " + e.Message;
            Etapa = EtapaPagamento.ErroGravacao;
            return;
        }
        finally
        {
            _gravandoPago = false;
        }
        await Concluir();
    }

    [RelayCommand]
    private Task GravarDeNovo() => Etapa == EtapaPagamento.ErroGravacao ? GravarPago() : Task.CompletedTask;

    /// <summary>
    /// O disco continua com erro: sai do programa. O pedido fica esperando e, ao abrir, o programa pergunta dele
    /// (nada é cobrado de novo).
    /// </summary>
    [RelayCommand]
    private void SairDoPrograma()
    {
        if (Etapa != EtapaPagamento.ErroGravacao || _gravandoPago) return;
        _principal.ExecutarProtegido(TelaProtegida.SairDoPrograma, _principal.Sair);
    }

    /// <summary>Cancela o pedido que não foi pago. Se nem isso o disco deixar, ele fica esperando e é resolvido ao abrir.</summary>
    private void CancelarPedido(long id)
    {
        try
        {
            _principal.Sistema.Vendas.Cancelar(id);
        }
        catch (Exception e)
        {
            Log.Erro("Cancelar pedido", e);
        }
    }

    [RelayCommand]
    private void CancelarMaquininha() => _cancelar?.Cancel();

    [RelayCommand]
    private void SimularAprovar() => (_principal.Sistema.Maquininha as MaquininhaSimulada)?.Aprovar();

    [RelayCommand]
    private void SimularRecusar() => (_principal.Sistema.Maquininha as MaquininhaSimulada)?.Recusar("Cartão recusado (simulação)");

    /// <summary>
    /// A maquininha separada aprovou: o operador confirma e as fichas saem. O botão aparece no lugar de Débito e
    /// Crédito: um toque duplo nessas formas não pode aprovar sem o cartão passar (ignora o primeiro meio segundo).
    /// </summary>
    [RelayCommand]
    private void ConfirmarNaMaquininha()
    {
        if (_cobrandoDesde is not { } desde || DateTime.UtcNow - desde < TimeSpan.FromMilliseconds(600)) return;
        (_principal.Sistema.Maquininha as MaquininhaSeparada)?.Aprovar();
    }

    [RelayCommand]
    private void NaoAprovou() =>
        (_principal.Sistema.Maquininha as MaquininhaSeparada)?.Recusar("A maquininha não aprovou o pagamento.");

    [RelayCommand]
    private void OutraForma()
    {
        if (Gravando) return;
        Mensagem = "";
        Etapa = EtapaPagamento.Escolher;
    }

    [RelayCommand]
    private Task TentarImprimir() => Imprimir();

    [RelayCommand]
    private void Fechar()
    {
        if (Etapa == EtapaPagamento.Maquininha || Gravando) return;
        FecharJanela();
    }

    /// <summary>Fecha o pagamento. Venda em dinheiro com troco: o troco continua na tela de venda por uns segundos.</summary>
    private void FecharJanela()
    {
        if (_fechada) return;
        _fechada = true;
        _principal.FecharDialogo(this);
        if (_pedido is { Forma: FormaPagamento.Dinheiro, TrocoCentavos: > 0 } pago)
            _principal.Venda.MostrarUltimoTroco(pago.TrocoCentavos, pago.RecebidoCentavos);
    }

    /// <summary>Pago: limpa o pedido da tela e imprime as fichas.</summary>
    private async Task Concluir()
    {
        _aoConcluir();
        // O troco da venda anterior (se ainda está na tela) não vale para esta: some, e o desta aparece ao fechar.
        _principal.Venda.EsconderTroco();
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
        var resultado = await Task.Run(() =>
        {
            var r = _principal.Sistema.ImprimirFichas(pedido, fichas);
            if (r.Ok) _principal.Sistema.Vendas.RegistrarImpressao(pedido.Id);
            return r;
        });
        Imprimindo = false;

        if (resultado.Ok)
        {
            ResultadoImpressao = resultado.Mensagem;
            // Com troco também fecha sozinho: o troco fica em cima do pedido, sem travar a próxima venda.
            _ = FecharSozinho();
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
        if (Etapa == EtapaPagamento.Concluido && !_fechada) FecharJanela();
    }

    private void AtualizarTroco()
    {
        var troco = Recebido.Centavos - TotalCentavos;
        FaltaDinheiro = troco < 0;
        Troco = troco < 0 ? "Falta " + Dinheiro.Formatar(-troco) : Dinheiro.Formatar(troco);
    }
}
