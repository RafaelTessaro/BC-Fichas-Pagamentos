using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Impressao;

namespace BCFichas.App.ViewModels;

/// <summary>Um item do pedido, com quantas fichas dele o cliente está devolvendo.</summary>
public sealed partial class ItemDevolucao(ItemPedido item, Action aoMudar) : ObservableObject
{
    public ItemPedido Item { get; } = item;
    public string Nome => Item.Nome;
    public string Preco => Dinheiro.Formatar(Item.PrecoCentavos) + " cada";
    public string Situacao => Item.Devolvidas == 0
        ? $"{Item.Quantidade} vendido(s)"
        : $"{Item.Quantidade} vendido(s) • {Item.Devolvidas} já devolvido(s)";
    public bool PodeDevolver => Item.PodeDevolver > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Total), nameof(Marcado))]
    private int _quantidade;

    public long TotalCentavos => Quantidade * Item.PrecoCentavos;
    public string Total => Dinheiro.Formatar(TotalCentavos);
    public bool Marcado => Quantidade > 0;

    partial void OnQuantidadeChanged(int value) => aoMudar();

    [RelayCommand]
    private void Mais()
    {
        if (Quantidade < Item.PodeDevolver) Quantidade++;
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
            foreach (var item in value.Itens) Itens.Add(new ItemDevolucao(item, Recalcular));
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
        var quantidades = Itens.Where(i => i.Quantidade > 0).ToDictionary(i => i.Item.Id, i => i.Quantidade);
        var pedidoId = Pedido.Id;

        Ocupado = true;
        try
        {
            var devolucao = Sistema.Devolucoes.Devolver(sessao, pedidoId, quantidades, Motivo);
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

    private void MarcarFicha(int sequencia)
    {
        // Mesma ordem do GeradorFichas: as fichas são numeradas item por item.
        var inicio = 0;
        foreach (var item in Itens)
        {
            var fichas = item.Item.Quantidade * Math.Max(1, item.Item.FichasPorUnidade);
            if (sequencia > inicio && sequencia <= inicio + fichas)
            {
                item.MaisCommand.Execute(null);
                return;
            }
            inicio += fichas;
        }
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
