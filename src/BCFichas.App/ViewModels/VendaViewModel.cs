using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Vendas;

namespace BCFichas.App.ViewModels;

public sealed partial class AbaItem(Aba aba) : ObservableObject
{
    public Aba Aba { get; } = aba;
    public string Nome => Aba.Nome;
    [ObservableProperty] private bool _ativa;
}

/// <summary>Um quadrado da grade de produtos (pode estar vazio).</summary>
public sealed class BotaoProduto
{
    public BotaoProduto(Produto? produto, Bitmap? imagem)
    {
        Produto = produto;
        Imagem = imagem;
        if (produto is null) return;
        var cor = Recursos.CorOuPadrao(produto.Cor);
        Fundo = new SolidColorBrush(cor);
        Texto = Recursos.TextoSobre(cor);
    }

    public Produto? Produto { get; }
    public bool Vazio => Produto is null;
    public bool Disponivel => Produto is { Esgotado: false };
    public string Nome => Produto?.Nome ?? "";
    /// <summary>Detalhe do produto; no combo sem detalhe, quantas fichas ele imprime.</summary>
    public string Detalhe => Produto is null ? ""
        : Produto.Detalhe.Length > 0 ? Produto.Detalhe
        : Produto.EhCombo ? $"COMBO • {Produto.FichasPorVenda} FICHAS" : "";
    public bool TemDetalhe => Detalhe.Length > 0;
    public string Preco => Produto is null ? "" : Dinheiro.Formatar(Produto.PrecoCentavos);
    public IBrush Fundo { get; } = Brushes.Transparent;
    public IBrush Texto { get; } = Brushes.White;
    public Bitmap? Imagem { get; }
    public bool TemImagem => Imagem is not null;
    public bool Esgotado => Produto?.Esgotado == true;
    public bool MostrarEstoque => Produto is { ControlaEstoque: true, Esgotado: false };
    public string Estoque => Produto is null ? "" : $"{Produto.Estoque} rest.";

    /// <summary>Letras menores quando o nome tem palavra comprida, para não quebrar no meio da palavra.</summary>
    public double TamanhoNome => MaiorPalavra switch { <= 8 => 21, <= 10 => 19, <= 12 => 17, _ => 15 };
    public double TamanhoNomeFoto => MaiorPalavra switch { <= 8 => 17, <= 10 => 15, <= 12 => 14, _ => 13 };

    private int MaiorPalavra =>
        Nome.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Length).DefaultIfEmpty(0).Max();
}

public sealed class LinhaItem(LinhaCarrinho linha)
{
    public long ProdutoId { get; } = linha.Produto.Id;
    public string Nome { get; } = linha.Produto.Nome;
    public string Unitario { get; } = Dinheiro.Formatar(linha.Produto.PrecoCentavos);
    public int Quantidade { get; } = linha.Quantidade;
    public string Total { get; } = Dinheiro.Formatar(linha.TotalCentavos);
}

/// <summary>Tela principal: abas, botões de produto, pedido e botão de pagamento.</summary>
public sealed partial class VendaViewModel : ViewModelBase
{
    /// <summary>Quanto tempo o último troco fica em cima do pedido.</summary>
    public static readonly TimeSpan TempoDoTroco = TimeSpan.FromSeconds(5);

    private readonly PrincipalViewModel _principal;
    private readonly Carrinho _carrinho = new();
    private readonly DispatcherTimer _timerTroco = new() { Interval = TempoDoTroco };

    public VendaViewModel(PrincipalViewModel principal)
    {
        _principal = principal;
        _timerTroco.Tick += (_, _) => EsconderTroco();
    }

    public PrincipalViewModel Principal => _principal;
    public ObservableCollection<AbaItem> Abas { get; } = new();
    public ObservableCollection<BotaoProduto> Botoes { get; } = new();
    public ObservableCollection<LinhaItem> Linhas { get; } = new();

    /// <summary>Grade da aba escolhida: colunas (0 = automática) e linhas.</summary>
    [ObservableProperty] private int _colunas;
    [ObservableProperty] private int _linhasGrade;
    /// <summary>A aba escolhida não tem produto: a tela explica onde cadastrar.</summary>
    [ObservableProperty] private bool _semProdutos;
    [ObservableProperty] private string _total = Dinheiro.Formatar(0);
    [ObservableProperty] private string _quantidadeTexto = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PagarCommand))]
    [NotifyCanExecuteChangedFor(nameof(LimparCommand))]
    private bool _vazio = true;

    public bool MostrarAbas => Abas.Count > 1;

    // Último troco (venda em dinheiro): fica em cima do pedido por uns segundos, sem tapar os botões.
    [ObservableProperty] private bool _mostrarTroco;
    [ObservableProperty] private string _ultimoTroco = "";
    [ObservableProperty] private string _ultimoRecebido = "";

    /// <summary>Depois de uma venda em dinheiro com troco: mostra quanto devolver ao cliente.</summary>
    public void MostrarUltimoTroco(long troco, long recebido)
    {
        if (troco <= 0) return;
        UltimoTroco = Dinheiro.Formatar(troco);
        UltimoRecebido = $"Recebido {Dinheiro.Formatar(recebido)}";
        MostrarTroco = true;
        _timerTroco.Stop();
        _timerTroco.Start();
    }

    public void EsconderTroco()
    {
        _timerTroco.Stop();
        MostrarTroco = false;
    }

    internal Carrinho Carrinho => _carrinho;

    public void Recarregar()
    {
        var abas = _principal.Sistema.Catalogo.Abas();
        var atual = Abas.FirstOrDefault(a => a.Ativa)?.Aba.Id;
        Abas.Clear();
        foreach (var aba in abas) Abas.Add(new AbaItem(aba));
        OnPropertyChanged(nameof(MostrarAbas));

        var selecionada = Abas.FirstOrDefault(a => a.Aba.Id == atual) ?? Abas.FirstOrDefault();
        if (selecionada is not null) SelecionarAba(selecionada);
        else
        {
            Botoes.Clear();
            SemProdutos = true;
        }

        AtualizarProdutosDoCarrinho();
    }

    /// <summary>Depois de uma venda: atualiza o "restam X" dos botões.</summary>
    public void AtualizarEstoque()
    {
        var aba = Abas.FirstOrDefault(a => a.Ativa);
        if (aba is not null) MontarBotoes(aba.Aba);
    }

    [RelayCommand]
    private void SelecionarAba(AbaItem aba)
    {
        foreach (var a in Abas) a.Ativa = a == aba;
        MontarBotoes(aba.Aba);
    }

    [RelayCommand]
    private void Adicionar(BotaoProduto botao)
    {
        if (botao.Produto is null) return;
        if (botao.Produto.Esgotado)
        {
            _principal.MostrarAviso($"{botao.Nome} está esgotado.", erro: true);
            return;
        }
        if (!_carrinho.Adicionar(botao.Produto))
        {
            _principal.MostrarAviso(SemEstoque(botao.Produto), erro: true);
            return;
        }
        AtualizarPedido();
    }

    private static string SemEstoque(Produto produto) => produto.EhCombo
        ? $"Não há estoque para mais um {produto.Nome}."
        : $"Só restam {produto.Estoque} de {produto.Nome}.";

    [RelayCommand]
    private void Mais(LinhaItem linha)
    {
        var produto = _carrinho.Linhas.FirstOrDefault(l => l.Produto.Id == linha.ProdutoId)?.Produto;
        if (produto is null) return;
        if (!_carrinho.Adicionar(produto))
            _principal.MostrarAviso(SemEstoque(produto), erro: true);
        AtualizarPedido();
    }

    [RelayCommand]
    private void Menos(LinhaItem linha)
    {
        _carrinho.Remover(linha.ProdutoId);
        AtualizarPedido();
    }

    [RelayCommand(CanExecute = nameof(TemItens))]
    private async Task Limpar()
    {
        if (_carrinho.QuantidadeItens > 1 &&
            !await _principal.Confirmar("Limpar pedido", "Tirar todos os itens do pedido?", "Limpar", "Voltar"))
            return;
        LimparPedido();
    }

    [RelayCommand(CanExecute = nameof(TemItens))]
    private void Pagar()
    {
        if (_principal.Sessao is null) return;
        _principal.AbrirDialogo(new PagamentoViewModel(_principal, _principal.Sessao, _carrinho.Linhas.ToList(),
            aoConcluir: LimparPedido));
    }

    [RelayCommand]
    private void AbrirMenu() => _principal.AbrirDialogo(new MenuViewModel(_principal));

    /// <summary>Barra lateral: sangria e suprimento (antes ficava no menu).</summary>
    [RelayCommand]
    private void AbrirSangria() =>
        _principal.AbrirProtegido(TelaProtegida.Sangria, () => new SangriaViewModel(_principal));

    /// <summary>Barra lateral: fechar o caixa (no modo teste, sair do teste).</summary>
    [RelayCommand]
    private void FecharCaixa()
    {
        if (_principal.ModoTeste) _ = _principal.SairDoModoTeste();
        else _principal.AbrirProtegido(TelaProtegida.FecharCaixa, () => new FechamentoViewModel(_principal));
    }

    /// <summary>Toque no logo da BC Fichas (5 toques seguidos ligam o modo teste).</summary>
    [RelayCommand]
    private void ToqueNaMarca() => _principal.ToqueNaMarca();

    [RelayCommand]
    private Task SairDoModoTeste() => _principal.SairDoModoTeste();

    public void LimparPedido()
    {
        _carrinho.Limpar();
        AtualizarPedido();
    }

    private bool TemItens() => !Vazio;

    /// <summary>
    /// Botões da aba na ordem das posições, sem espaço vazio: a grade da tela (<see cref="Views.GradeDeBotoes"/>)
    /// divide a área entre eles. Com colunas × linhas, cabem no máximo colunas × linhas.
    /// </summary>
    private void MontarBotoes(Aba aba)
    {
        Colunas = aba.Colunas;
        LinhasGrade = aba.Linhas;
        var produtos = _principal.Sistema.Catalogo.ProdutosDaAba(aba.Id)
            .OrderBy(p => p.Posicao).ThenBy(p => p.Nome)
            .Take(aba.Capacidade);
        Botoes.Clear();
        foreach (var p in produtos)
            Botoes.Add(new BotaoProduto(p, CacheImagens.Obter(_principal.Sistema.Impressao.CaminhoImagem(p.Imagem), 240)));
        SemProdutos = Botoes.Count == 0;
    }

    /// <summary>O estoque dos produtos no pedido pode ter mudado (venda, edição).</summary>
    private void AtualizarProdutosDoCarrinho()
    {
        var antes = _carrinho.Linhas.Select(l => (l.Produto.Id, l.Quantidade)).ToList();
        _carrinho.Limpar();
        foreach (var (id, quantidade) in antes)
        {
            var atual = _principal.Sistema.Catalogo.Produto(id);
            if (atual is not { Ativo: true }) continue;
            // Põe de volta o quanto ainda cabe no estoque (contando os produtos dos combos).
            var cabe = quantidade;
            while (cabe > 0 && !_carrinho.Adicionar(atual, cabe)) cabe--;
        }
        AtualizarPedido();
    }

    private void AtualizarPedido()
    {
        Linhas.Clear();
        foreach (var linha in _carrinho.Linhas.Where(l => l.Quantidade > 0)) Linhas.Add(new LinhaItem(linha));
        Total = Dinheiro.Formatar(_carrinho.TotalCentavos);
        var qtd = _carrinho.QuantidadeItens;
        QuantidadeTexto = qtd == 0 ? "" : qtd == 1 ? "1 item" : $"{qtd} itens";
        Vazio = qtd == 0;
    }
}
