using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Vendas;

namespace BCFichas.App.ViewModels;

public sealed class PedidoItem(Pedido p)
{
    public Pedido Pedido { get; } = p;
    public string Numero { get; } = $"#{p.Numero:000000}";
    public string Hora { get; } = Formato.Hora(p.CriadoEm);
    public string Forma { get; } = Nomes.De(p.Forma);
    public string Total { get; } = Dinheiro.Formatar(p.TotalCentavos);
    public string Status { get; } = p.Status == StatusPedido.Pago
        ? p.Impressoes == 0 ? "Pago • não impresso" : $"Pago • impresso {p.Impressoes}x"
        : Nomes.De(p.Status);
    public bool Teste { get; } = p.Teste;
    public bool Pago { get; } = p.Status == StatusPedido.Pago;
    public bool NaoImpresso { get; } = p.Status == StatusPedido.Pago && p.Impressoes == 0;
}

public sealed class ItemReimpressao(ItemPedido item)
{
    public ItemPedido Item { get; } = item;
    public string Nome { get; } = item.Nome;
    public string Detalhe { get; } = item.Detalhe;
    public string Quantidade { get; } = $"{item.Quantidade} x {Dinheiro.Formatar(item.PrecoCentavos)}";

    /// <summary>Fichas que ainda valem (as devolvidas não são reimpressas).</summary>
    public string Fichas { get; } = Texto(item);

    public bool TemFichas { get; } = Validas(item) > 0;

    private static int Total(ItemPedido i) => i.Quantidade * Math.Max(1, i.FichasPorUnidade);
    private static int Devolvidas(ItemPedido i) =>
        i.EhCombo ? i.Componentes.Sum(c => c.Devolvidas) : i.Devolvidas * Math.Max(1, i.FichasPorUnidade);
    private static int Validas(ItemPedido i) => Total(i) - Devolvidas(i);

    private static string Texto(ItemPedido i)
    {
        var texto = Devolvidas(i) == 0 ? $"{Total(i)} ficha(s)" : $"{Validas(i)} ficha(s) • {Devolvidas(i)} devolvida(s)";
        // Combo: diz quais fichas saem (ex.: 5 HEINEKEN)
        if (i.EhCombo) texto += " • " + string.Join(", ", i.Componentes.Select(c => $"{c.Quantidade * i.Quantidade} {c.Nome}"));
        return texto;
    }
}

/// <summary>
/// Segunda via das fichas de um pedido (inteiro ou de um item). Com <paramref name="soNaoImpressos"/>: só os pedidos
/// pagos cujas fichas não saíram (a impressora falhou) e só o pedido inteiro, uma vez — é o que aparece no menu
/// quando a reimpressão não está liberada para o cliente.
/// </summary>
public sealed partial class ReimpressaoViewModel(PrincipalViewModel principal, bool soNaoImpressos = false)
    : PaginaViewModel(principal)
{
    public bool SoNaoImpressos { get; } = soNaoImpressos;
    public string Titulo => SoNaoImpressos ? "Fichas não impressas" : "Reimprimir fichas";
    public string TextoImprimirTudo => SoNaoImpressos ? "Imprimir as fichas do pedido" : "Reimprimir todas as fichas do pedido";

    public ObservableCollection<PedidoItem> Pedidos { get; } = new();
    public ObservableCollection<ItemReimpressao> Itens { get; } = new();

    [ObservableProperty] private string _busca = "";
    [ObservableProperty] private PedidoItem? _selecionado;
    [ObservableProperty] private bool _ocupado;

    public bool TemSelecionado => Selecionado is not null;
    public bool PodeReimprimir => Selecionado?.Pago == true && !Ocupado;
    public bool PodeReimprimirItem => PodeReimprimir && !SoNaoImpressos;
    public bool Vazio => Pedidos.Count == 0;

    public override void AoAbrir() => Atualizar();

    partial void OnBuscaChanged(string value) => Atualizar();

    partial void OnOcupadoChanged(bool value)
    {
        OnPropertyChanged(nameof(PodeReimprimir));
        OnPropertyChanged(nameof(PodeReimprimirItem));
    }

    partial void OnSelecionadoChanged(PedidoItem? value)
    {
        Itens.Clear();
        if (value is not null)
        {
            var pedido = Sistema.Vendas.Pedido(value.Pedido.Id);
            if (pedido is not null)
                foreach (var item in pedido.Itens) Itens.Add(new ItemReimpressao(item));
        }
        OnPropertyChanged(nameof(TemSelecionado));
        OnPropertyChanged(nameof(PodeReimprimir));
        OnPropertyChanged(nameof(PodeReimprimirItem));
    }

    [RelayCommand]
    private void Atualizar()
    {
        var sessao = Principal.Sessao;
        var id = Selecionado?.Pedido.Id;
        Pedidos.Clear();
        if (sessao is null) return;
        long? numero = long.TryParse(Busca.Trim().TrimStart('#'), out var n) ? n : null;
        foreach (var p in Sistema.Vendas.Pedidos(sessao.Id, numero, soNaoImpressos: SoNaoImpressos))
            Pedidos.Add(new PedidoItem(p));
        Selecionado = Pedidos.FirstOrDefault(p => p.Pedido.Id == id) ?? Pedidos.FirstOrDefault();
        OnPropertyChanged(nameof(Vazio));
    }

    [RelayCommand]
    private Task ReimprimirTudo() => Reimprimir(null);

    [RelayCommand]
    private Task ReimprimirItem(ItemReimpressao item) => SoNaoImpressos ? Task.CompletedTask : Reimprimir(item.Item.Id);

    private async Task Reimprimir(long? itemId)
    {
        if (Selecionado is not { Pago: true } || Ocupado) return;
        var pedido = Sistema.Vendas.Pedido(Selecionado.Pedido.Id);
        if (pedido is null || (SoNaoImpressos && pedido.Impressoes > 0)) return;

        Ocupado = true;
        try
        {
            // Pedido que nunca saiu na impressora é impressão normal, não reimpressão.
            var reimpressao = pedido.Impressoes > 0;
            var fichas = GeradorFichas.Gerar(pedido, Principal.Config, reimpressao, itemId);
            if (fichas.Count == 0)
            {
                Principal.MostrarAviso("As fichas deste pedido foram devolvidas: não há o que reimprimir.", erro: true);
                return;
            }
            var resultado = await Principal.ImprimirAsync(() => Sistema.ImprimirFichas(pedido, fichas));
            if (resultado.Ok)
            {
                Sistema.Vendas.RegistrarImpressao(pedido.Id);
                Principal.MostrarAviso(resultado.Mensagem);
                Atualizar();
            }
        }
        finally
        {
            Ocupado = false;
        }
    }
}

public sealed class SessaoItem(SessaoCaixa s, long total)
{
    public SessaoCaixa Sessao { get; } = s;
    public string Titulo { get; } = Formato.Caixa(s.Caixa, s.Operador);
    public string Periodo { get; } = Formato.DataHora(s.AbertaEm) +
                                      (s.FechadaEm is { } f ? " → " + Formato.Hora(f) : " • aberto");
    public string Total { get; } = Dinheiro.Formatar(total);
    public bool Aberta { get; } = s.Aberta;
}

/// <summary>Gerencial: caixa atual, caixas anteriores e sangrias.</summary>
public sealed partial class RelatoriosViewModel(PrincipalViewModel principal) : PaginaViewModel(principal)
{
    [ObservableProperty] private int _abaSelecionada;
    [ObservableProperty] private ResumoVM? _atual;
    [ObservableProperty] private SessaoItem? _sessaoSelecionada;
    [ObservableProperty] private ResumoVM? _resumoSelecionado;
    [ObservableProperty] private int _dias = 1;

    public ObservableCollection<SessaoItem> Sessoes { get; } = new();
    public ObservableCollection<MovimentoItem> Movimentos { get; } = new();
    public string TotalMovimentos { get; private set; } = "";

    public bool FiltroHoje => Dias == 1;
    public bool Filtro7 => Dias == 7;
    public bool Filtro30 => Dias == 30;
    public bool FiltroTudo => Dias == 0;

    public override void AoAbrir() => Atualizar();

    partial void OnDiasChanged(int value)
    {
        OnPropertyChanged(nameof(FiltroHoje));
        OnPropertyChanged(nameof(Filtro7));
        OnPropertyChanged(nameof(Filtro30));
        OnPropertyChanged(nameof(FiltroTudo));
        Atualizar();
    }

    partial void OnSessaoSelecionadaChanged(SessaoItem? value) =>
        ResumoSelecionado = value is null ? null : new ResumoVM(Sistema.Caixa.Resumo(value.Sessao.Id));

    [RelayCommand]
    private void Filtrar(string dias) => Dias = int.Parse(dias, System.Globalization.CultureInfo.InvariantCulture);

    [RelayCommand]
    private void Atualizar()
    {
        Atual = Principal.Sessao is { } s ? new ResumoVM(Sistema.Caixa.Resumo(s.Id)) : null;

        var ate = DateTime.Today;
        var de = Dias == 0 ? new DateTime(2000, 1, 1) : ate.AddDays(-(Dias - 1));
        var id = SessaoSelecionada?.Sessao.Id;
        Sessoes.Clear();
        // O total de cada caixa numa consulta só (antes era o resumo inteiro de cada caixa, um por um)
        var totais = Sistema.Caixa.TotaisVendidos(de, ate);
        foreach (var sessao in Sistema.Caixa.Sessoes(de, ate))
            Sessoes.Add(new SessaoItem(sessao, totais.GetValueOrDefault(sessao.Id)));
        SessaoSelecionada = Sessoes.FirstOrDefault(x => x.Sessao.Id == id) ?? Sessoes.FirstOrDefault();

        Movimentos.Clear();
        var movimentos = Sistema.Caixa.Movimentos(de, ate);
        foreach (var m in movimentos)
            Movimentos.Add(new MovimentoItem($"{Formato.DataHora(m.CriadoEm)} • {Formato.Caixa(m.Caixa, m.Usuario)}",
                m.NomeCurto, (m.Saida ? "− " : "+ ") + Dinheiro.Formatar(m.ValorCentavos), m.Motivo, m.Saida));
        long Soma(TipoMovimento tipo) => movimentos.Where(m => m.Tipo == tipo).Sum(m => m.ValorCentavos);
        TotalMovimentos = $"Sangrias: {Dinheiro.Formatar(Soma(TipoMovimento.Sangria))}   •   " +
                          $"Suprimentos: {Dinheiro.Formatar(Soma(TipoMovimento.Suprimento))}";
        var devolvido = Soma(TipoMovimento.Devolucao);
        if (devolvido > 0) TotalMovimentos += $"   •   Devoluções: {Dinheiro.Formatar(devolvido)}";
        OnPropertyChanged(nameof(TotalMovimentos));
    }

    [RelayCommand]
    private Task ImprimirAtual()
    {
        if (Principal.Sessao is not { } s) return Task.CompletedTask;
        var resumo = Sistema.Caixa.Resumo(s.Id);
        return Principal.ImprimirAsync(() =>
            Sistema.Impressao.Documento(Relatorios.Fechamento(resumo, Principal.Config, parcial: true), "Parcial impressa"));
    }

    [RelayCommand]
    private Task ImprimirSelecionado()
    {
        if (ResumoSelecionado is null) return Task.CompletedTask;
        var resumo = ResumoSelecionado.Resumo;
        return Principal.ImprimirAsync(() =>
            Sistema.Impressao.Documento(Relatorios.Fechamento(resumo, Principal.Config, parcial: resumo.Sessao.Aberta),
                "Relatório impresso"));
    }
}
