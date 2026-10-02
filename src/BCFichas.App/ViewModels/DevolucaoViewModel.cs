using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Vendas;

namespace BCFichas.App.ViewModels;

/// <summary>
/// Um produto do pedido, com quantas unidades o cliente está devolvendo. No combo, cada produto das fichas é
/// uma linha e a devolução é ficha por ficha (vale a parte do preço do combo).
/// </summary>
public sealed partial class ItemDevolucao(LinhaDeFichas linha, Action aoMudar) : ObservableObject
{
    public LinhaDeFichas Linha { get; } = linha;
    public string Nome => Linha.Nome;
    public bool DeCombo => Linha.DeCombo;
    public string Combo => Linha.DeCombo ? "Ficha do " + Linha.Item.Nome : "";
    public string Preco => Dinheiro.Formatar(Linha.ValorUnitario) + (Linha.DeCombo ? " cada (parte do combo)" : " cada");
    public string Situacao => (Linha.DeCombo ? $"{Linha.Total} ficha(s)" : $"{Linha.Total} vendido(s)") +
                              (Linha.Devolvidas == 0 ? "" : $" • {Linha.Devolvidas} já devolvida(s)");
    public bool PodeDevolver => Linha.PodeDevolver > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Total), nameof(Marcado))]
    private int _quantidade;

    public long TotalCentavos => Linha.Valor(Quantidade);
    public string Total => Dinheiro.Formatar(TotalCentavos);
    public bool Marcado => Quantidade > 0;

    partial void OnQuantidadeChanged(int value) => aoMudar();

    [RelayCommand]
    private void Mais()
    {
        if (Quantidade < Linha.PodeDevolver) Quantidade++;
    }

    [RelayCommand]
    private void Menos()
    {
        if (Quantidade > 0) Quantidade--;
    }
}

public sealed record DevolucaoFeita(string Hora, string Titulo, string Itens, string Valor, string Como);

/// <summary>
/// Devolução de fichas não usadas: o cliente traz a ficha, o caixa acha o pedido pelo número (ou pelo código
/// de barras) e marca o que voltou. Dinheiro sai da gaveta; cartão e PIX são estornados na maquininha.
/// </summary>
public sealed partial class DevolucaoViewModel(PrincipalViewModel principal) : PaginaViewModel(principal)
{
    public ObservableCollection<ItemDevolucao> Itens { get; } = new();
    public ObservableCollection<DevolucaoFeita> Feitas { get; } = new();

    [ObservableProperty] private string _busca = "";
    [ObservableProperty] private Pedido? _pedido;
    [ObservableProperty] private string _motivo = "";
    [ObservableProperty] private bool _estornoFeito;
    [ObservableProperty] private bool _ocupado;
    [ObservableProperty] private string _dinheiroEmCaixa = "";
    [ObservableProperty] private string _totalTexto = Dinheiro.Formatar(0);
    [ObservableProperty] private long _totalCentavos;

    public bool TemPedido => Pedido is not null;
    public bool SemPedido => Pedido is null;
    public bool SemFeitas => Feitas.Count == 0;
    public bool EmDinheiro => Pedido?.Forma == FormaPagamento.Dinheiro;
    public bool NaMaquininha => Pedido is not null && !EmDinheiro;
    public string FormaTexto => Pedido is null ? "" : Nomes.De(Pedido.Forma);

    public string PedidoTitulo => Pedido is null ? "" : $"Pedido {Pedido.Numero}";
    public string PedidoInfo => Pedido is null
        ? ""
        : $"{Formato.DataHora(Pedido.CriadoEm)} • Caixa {Pedido.Caixa:00} • {Dinheiro.Formatar(Pedido.TotalCentavos)} em {Nomes.De(Pedido.Forma)}";

    /// <summary>O que o operador tem de fazer com o dinheiro.</summary>
    public string Instrucao => Pedido is null || TotalCentavos == 0
        ? "Marque as fichas que o cliente devolveu."
        : EmDinheiro
            ? $"Devolva {TotalTexto} em dinheiro ao cliente. O valor sai da gaveta deste caixa."
            : $"Faça o estorno de {TotalTexto} no {Nomes.De(Pedido.Forma).ToUpperInvariant()} pela maquininha de cartão. " +
              "O dinheiro da gaveta não muda.";

    public bool PodeRegistrar => TotalCentavos > 0 && !Ocupado && (EmDinheiro || EstornoFeito);

    public override void AoAbrir() => Atualizar();

    partial void OnPedidoChanged(Pedido? value)
    {
        Itens.Clear();
        if (value is not null)
            foreach (var linha in FichasDoPedido.Linhas(value)) Itens.Add(new ItemDevolucao(linha, Recalcular));
        Motivo = "";
        EstornoFeito = false;
        OnPropertyChanged(nameof(TemPedido));
        OnPropertyChanged(nameof(SemPedido));
        OnPropertyChanged(nameof(EmDinheiro));
        OnPropertyChanged(nameof(NaMaquininha));
        OnPropertyChanged(nameof(FormaTexto));
        OnPropertyChanged(nameof(PedidoTitulo));
        OnPropertyChanged(nameof(PedidoInfo));
        Recalcular();
    }

    partial void OnEstornoFeitoChanged(bool value) => OnPropertyChanged(nameof(PodeRegistrar));
    partial void OnOcupadoChanged(bool value) => OnPropertyChanged(nameof(PodeRegistrar));

    /// <summary>Teclado numérico do número do pedido.</summary>
    [RelayCommand]
    private void Tecla(string tecla)
    {
        if (tecla == "<") Busca = Busca.Length > 0 ? Busca[..^1] : "";
        else if (tecla == "C") Busca = "";
        else if (Busca.Length < 14 && tecla.All(char.IsAsciiDigit)) Busca += tecla;
    }

    /// <summary>
    /// Acha o pedido. Aceita o número da ficha ("13" ou "PED: 13") ou o código de barras da ficha
    /// (caixa + pedido + sequência, 11 números): aí já marca a ficha lida.
    /// </summary>
    [RelayCommand]
    private void Buscar()
    {
        var texto = new string(Busca.Where(char.IsAsciiDigit).ToArray());
        if (texto.Length == 0)
        {
            Principal.MostrarAviso("Digite o número do pedido que está na ficha (PED).", erro: true);
            return;
        }

        int? sequencia = null;
        long numero;
        if (texto.Length == 11)
        {
            numero = long.Parse(texto.Substring(2, 6), CultureInfo.InvariantCulture);
            sequencia = int.Parse(texto[8..], CultureInfo.InvariantCulture);
        }
        else if (!long.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out numero))
        {
            Principal.MostrarAviso("Número de pedido inválido.", erro: true);
            return;
        }

        var achado = Sistema.Vendas.PedidoPorNumero(numero, Principal.ModoTeste);
        if (achado is null)
        {
            Principal.MostrarAviso($"Pedido {numero} não encontrado neste caixa.", erro: true);
            return;
        }
        if (achado.Status != StatusPedido.Pago)
        {
            Principal.MostrarAviso($"O pedido {numero} não foi pago ({Nomes.De(achado.Status).ToLowerInvariant()}).", erro: true);
            return;
        }

        Pedido = achado;
        Busca = "";
        if (sequencia is { } seq) MarcarFicha(seq);
        if (Itens.All(i => !i.PodeDevolver))
            Principal.MostrarAviso("Todas as fichas deste pedido já foram devolvidas.", erro: true);
    }

    [RelayCommand]
    private void OutroPedido()
    {
        Pedido = null;
        Busca = "";
    }

    [RelayCommand]
    private async Task Registrar()
    {
        var sessao = Principal.Sessao;
        if (sessao is null || Pedido is null || !PodeRegistrar) return;
        var devolvidas = Itens.Where(i => i.Quantidade > 0)
            .Select(i => (i.Linha.Item.Id, i.Linha.Componente?.Id, i.Quantidade)).ToList();
        var pedidoId = Pedido.Id;

        Ocupado = true;
        try
        {
            var devolucao = Sistema.Devolucoes.Devolver(sessao, pedidoId, devolvidas, Motivo);
            var pedido = Sistema.Vendas.Pedido(pedidoId)!;
            var noCaixa = Sistema.Caixa.Resumo(sessao.Id).DinheiroEsperado;
            Principal.MostrarAviso(devolucao.EmDinheiro
                ? $"Devolução registrada: entregue {Dinheiro.Formatar(devolucao.ValorCentavos)} ao cliente."
                : $"Devolução registrada ({Dinheiro.Formatar(devolucao.ValorCentavos)} estornado na maquininha).");
            Pedido = null;
            Atualizar();

            var config = Principal.Config;
            await Principal.ImprimirAsync(() =>
                Sistema.Impressao.Documento(Relatorios.Devolucao(devolucao, pedido, config, noCaixa), "Comprovante impresso"));
        }
        catch (ErroDeNegocio e)
        {
            Principal.MostrarAviso(e.Message, erro: true);
        }
        finally
        {
            Ocupado = false;
        }
    }

    /// <summary>Ficha lida no leitor: marca uma unidade da linha que imprimiu essa ficha.</summary>
    private void MarcarFicha(int sequencia)
    {
        if (Pedido is null || FichasDoPedido.LinhaDaFicha(Pedido, sequencia) is not { } linha) return;
        Itens.FirstOrDefault(i => i.Linha.Item.Id == linha.Item.Id && i.Linha.Componente?.Id == linha.Componente?.Id)
            ?.MaisCommand.Execute(null);
    }

    private void Recalcular()
    {
        TotalCentavos = Itens.Sum(i => i.TotalCentavos);
        TotalTexto = Dinheiro.Formatar(TotalCentavos);
        OnPropertyChanged(nameof(Instrucao));
        OnPropertyChanged(nameof(PodeRegistrar));
    }

    private void Atualizar()
    {
        Feitas.Clear();
        var sessao = Principal.Sessao;
        if (sessao is not null)
        {
            DinheiroEmCaixa = Dinheiro.Formatar(Sistema.Caixa.Resumo(sessao.Id).DinheiroEsperado);
            foreach (var d in Sistema.Devolucoes.Devolucoes(sessao.Id))
                Feitas.Add(new DevolucaoFeita(Formato.Hora(d.CriadoEm), $"Pedido {d.NumeroPedido}",
                    string.Join(", ", d.Itens.Select(i => $"{i.Quantidade} x {i.Nome}")),
                    "− " + Dinheiro.Formatar(d.ValorCentavos),
                    d.EmDinheiro ? "Dinheiro (gaveta)" : $"{Nomes.De(d.Forma)} (maquininha)"));
        }
        OnPropertyChanged(nameof(SemFeitas));
    }
}
