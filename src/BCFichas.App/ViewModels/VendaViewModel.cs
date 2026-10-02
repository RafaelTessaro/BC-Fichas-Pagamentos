using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
    public string Detalhe => Produto?.Detalhe ?? "";
    public bool TemDetalhe => !string.IsNullOrEmpty(Produto?.Detalhe);
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
    private readonly PrincipalViewModel _principal;
    private readonly Carrinho _carrinho = new();

    public VendaViewModel(PrincipalViewModel principal)
    {
        _principal = principal;
    }

    public PrincipalViewModel Principal => _principal;
    public ObservableCollection<AbaItem> Abas { get; } = new();
    public ObservableCollection<BotaoProduto> Botoes { get; } = new();
    public ObservableCollection<LinhaItem> Linhas { get; } = new();

    [ObservableProperty] private int _colunas = 4;
    [ObservableProperty] private int _linhasGrade = 3;
    [ObservableProperty] private string _total = Dinheiro.Formatar(0);
    [ObservableProperty] private string _quantidadeTexto = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PagarCommand))]
    [NotifyCanExecuteChangedFor(nameof(LimparCommand))]
    private bool _vazio = true;

    public bool MostrarAbas => Abas.Count > 1;

    internal Carrinho Carrinho => _carrinho;

    public void Recarregar()
    {
        var config = _principal.Config;
        Colunas = config.Colunas;
        LinhasGrade = config.Linhas;

        var abas = _principal.Sistema.Catalogo.Abas();
        var atual = Abas.FirstOrDefault(a => a.Ativa)?.Aba.Id;
        Abas.Clear();
        foreach (var aba in abas) Abas.Add(new AbaItem(aba));
        OnPropertyChanged(nameof(MostrarAbas));

        var selecionada = Abas.FirstOrDefault(a => a.Aba.Id == atual) ?? Abas.FirstOrDefault();
        if (selecionada is not null) SelecionarAba(selecionada);
        else Botoes.Clear();

        AtualizarProdutosDoCarrinho();
    }

    /// <summary>Depois de uma venda: atualiza o "restam X" dos botões.</summary>
    public void AtualizarEstoque()
    {
        var aba = Abas.FirstOrDefault(a => a.Ativa);
        if (aba is not null) MontarBotoes(aba.Aba.Id);
    }

    [RelayCommand]
    private void SelecionarAba(AbaItem aba)
    {
        foreach (var a in Abas) a.Ativa = a == aba;
        MontarBotoes(aba.Aba.Id);
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
            _principal.MostrarAviso($"Só restam {botao.Produto.Estoque} de {botao.Nome}.", erro: true);
            return;
        }
        AtualizarPedido();
    }

    [RelayCommand]
    private void Mais(LinhaItem linha)
    {
        var produto = _carrinho.Linhas.FirstOrDefault(l => l.Produto.Id == linha.ProdutoId)?.Produto;
        if (produto is null) return;
        if (!_carrinho.Adicionar(produto))
            _principal.MostrarAviso($"Só restam {produto.Estoque} de {produto.Nome}.", erro: true);
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

    public void LimparPedido()
    {
        _carrinho.Limpar();
        AtualizarPedido();
    }

    private bool TemItens() => !Vazio;

    private void MontarBotoes(long abaId)
    {
        var total = Colunas * LinhasGrade;
        var produtos = _principal.Sistema.Catalogo.ProdutosDaAba(abaId);
        var grade = new Produto?[total];
        var sobra = new List<Produto>();
        foreach (var p in produtos)
        {
            if (p.Posicao >= 1 && p.Posicao <= total && grade[p.Posicao - 1] is null) grade[p.Posicao - 1] = p;
            else sobra.Add(p);
        }
        // Produtos fora da grade (grade diminuiu) vão para os espaços livres.
        for (var i = 0; i < total && sobra.Count > 0; i++)
        {
            if (grade[i] is not null) continue;
            grade[i] = sobra[0];
            sobra.RemoveAt(0);
        }

        Botoes.Clear();
        foreach (var p in grade)
            Botoes.Add(new BotaoProduto(p, CacheImagens.Obter(_principal.Sistema.Impressao.CaminhoImagem(p?.Imagem), 240)));
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
            var cabe = atual.ControlaEstoque ? Math.Min(quantidade, Math.Max(atual.Estoque, 0)) : quantidade;
            if (cabe > 0) _carrinho.Adicionar(atual, cabe);
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
