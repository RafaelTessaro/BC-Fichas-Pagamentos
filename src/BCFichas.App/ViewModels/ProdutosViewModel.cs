using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Impressao;

namespace BCFichas.App.ViewModels;

public sealed class ProdutoItem(Produto p, string aba)
{
    public Produto Produto { get; } = p;
    public string Nome { get; } = p.Nome;
    public string Info { get; } = $"{aba} • posição {p.Posicao}" + (p.Ativo ? "" : " • INATIVO");
    public string Preco { get; } = Dinheiro.Formatar(p.PrecoCentavos);
    public string Estoque { get; } = p.ControlaEstoque ? $"Estoque: {p.Estoque}" : "";
    public IBrush Cor { get; } = new SolidColorBrush(Recursos.CorOuPadrao(p.Cor));
    public double Opacidade { get; } = p.Ativo ? 1 : 0.5;
}

public sealed partial class OpcaoCor(string hex) : ObservableObject
{
    public string Hex { get; } = hex;
    public IBrush Pincel { get; } = new SolidColorBrush(Color.Parse(hex));
    /// <summary>Cor clara (branco): ganha contorno para não sumir no fundo branco.</summary>
    public bool Clara { get; } = Recursos.TextoSobre(Color.Parse(hex)) != Brushes.White;
    [ObservableProperty] private bool _marcada;
}

/// <summary>Cadastro dos produtos (botões da tela de venda).</summary>
public sealed partial class ProdutosViewModel : PaginaViewModel
{
    private static readonly string[] Paleta =
    [
        "#0E9F6E", "#2B8A3E", "#1098AD", "#1971C2", "#4F46E5", "#7C3AED", "#9C36B5", "#D6336C",
        "#C92A2A", "#E8590C", "#F08C00", "#E8B200", "#A0522D", "#475569", "#0F172A", "#FFFFFF",
    ];

    private bool _carregando;

    public ProdutosViewModel(PrincipalViewModel principal) : base(principal)
    {
        Cores = Paleta.Select(c => new OpcaoCor(c)).ToList();
    }

    public ObservableCollection<ProdutoItem> Lista { get; } = new();
    public ObservableCollection<Aba> Abas { get; } = new();
    public ObservableCollection<int> Posicoes { get; } = new();
    public List<OpcaoCor> Cores { get; }
    public List<int> OpcoesFichas { get; } = Enumerable.Range(1, 10).ToList();

    [ObservableProperty] private string _busca = "";
    [ObservableProperty] private ProdutoItem? _selecionado;
    [ObservableProperty] private string _titulo = "Novo produto";
    [ObservableProperty] private long _id;
    [ObservableProperty] private string _nome = "";
    [ObservableProperty] private string _detalhe = "";
    [ObservableProperty] private Aba? _aba;
    // Anulável: ao trocar a lista de posições o ComboBox limpa a seleção (manda null) antes de escolhermos de novo
    [ObservableProperty] private int? _posicao;
    /// <summary>Explica por que não há posição para escolher (aba cheia). Vazio quando está tudo certo.</summary>
    [ObservableProperty] private string _avisoPosicao = "";
    [ObservableProperty] private int _fichasPorUnidade = 1;
    [ObservableProperty] private string _preco = "";
    [ObservableProperty] private string _custo = "";
    [ObservableProperty] private bool _controlaEstoque;
    [ObservableProperty] private string _estoque = "0";
    [ObservableProperty] private bool _ativo = true;
    [ObservableProperty] private string _cor = Paleta[0];
    [ObservableProperty] private string? _imagem;
    [ObservableProperty] private Bitmap? _imagemPrevia;
    [ObservableProperty] private IBrush _previaFundo = new SolidColorBrush(Color.Parse(Paleta[0]));
    [ObservableProperty] private IBrush _previaTexto = Brushes.White;

    public bool Editando => Id != 0;
    public bool TemImagem => Imagem is not null;
    private int TotalPosicoes => Principal.Config.Colunas * Principal.Config.Linhas;

    public override void AoAbrir()
    {
        Abas.Clear();
        foreach (var a in Sistema.Catalogo.Abas()) Abas.Add(a);
        Atualizar();
        Novo();
    }

    partial void OnBuscaChanged(string value) => Atualizar();

    partial void OnSelecionadoChanged(ProdutoItem? value)
    {
        if (value is not null) Carregar(value.Produto);
    }

    partial void OnAbaChanged(Aba? value)
    {
        if (!_carregando) AtualizarPosicoes(escolherLivre: true);
    }

    partial void OnIdChanged(long value) => OnPropertyChanged(nameof(Editando));

    partial void OnCorChanged(string value)
    {
        foreach (var c in Cores) c.Marcada = c.Hex == value;
        var cor = Recursos.CorOuPadrao(value);
        PreviaFundo = new SolidColorBrush(cor);
        PreviaTexto = Recursos.TextoSobre(cor);
    }

    partial void OnImagemChanged(string? value)
    {
        ImagemPrevia = CacheImagens.Obter(Sistema.Impressao.CaminhoImagem(value), 200);
        OnPropertyChanged(nameof(TemImagem));
    }

    [RelayCommand]
    private void Novo()
    {
        _carregando = true;
        Selecionado = null;
        Id = 0;
        Titulo = "Novo produto";
        Nome = "";
        Detalhe = "";
        Aba = Abas.FirstOrDefault();
        FichasPorUnidade = 1;
        Preco = "";
        Custo = "";
        ControlaEstoque = false;
        Estoque = "0";
        Ativo = true;
        Cor = Paleta[0];
        Imagem = null;
        _carregando = false;
        AtualizarPosicoes(escolherLivre: true);
    }

    [RelayCommand]
    private void EscolherCor(OpcaoCor cor) => Cor = cor.Hex;

    [RelayCommand]
    private void Salvar()
    {
        if (!Dinheiro.TentarLer(Preco, out var preco))
        {
            Principal.MostrarAviso("Digite o preço, por exemplo 10,00.", erro: true);
            return;
        }
        long custo = 0;
        if (!string.IsNullOrWhiteSpace(Custo) && !Dinheiro.TentarLer(Custo, out custo))
        {
            Principal.MostrarAviso("Custo inválido.", erro: true);
            return;
        }
        if (!int.TryParse(Estoque, out var estoque)) estoque = 0;
        if (Posicao is not { } posicao)
        {
            Principal.MostrarAviso(AvisoPosicao.Length > 0 ? AvisoPosicao : "Escolha a posição do botão na tela.", erro: true);
            return;
        }

        try
        {
            var produto = Sistema.Catalogo.SalvarProduto(new Produto
            {
                Id = Id,
                Nome = Nome,
                Detalhe = Detalhe,
                AbaId = Aba?.Id ?? 0,
                Posicao = posicao,
                FichasPorUnidade = FichasPorUnidade,
                PrecoCentavos = preco,
                CustoCentavos = custo,
                ControlaEstoque = ControlaEstoque,
                Estoque = estoque,
                Ativo = Ativo,
                Cor = Cor,
                Imagem = Imagem,
            }, TotalPosicoes);
            Principal.MostrarAviso($"{produto.Nome} salvo.");
            var novo = Id == 0;
            Atualizar();
            if (novo) Novo();
            else Selecionado = Lista.FirstOrDefault(p => p.Produto.Id == produto.Id);
        }
        catch (ErroDeNegocio e)
        {
            Principal.MostrarAviso(e.Message, erro: true);
        }
    }

    [RelayCommand]
    private async Task Excluir()
    {
        if (Id == 0) return;
        if (!await Principal.Confirmar("Excluir produto", $"Excluir {Nome}? As vendas já feitas continuam nos relatórios.",
                "Excluir", "Voltar", perigo: true))
            return;
        Sistema.Catalogo.ExcluirProduto(Id);
        Principal.MostrarAviso($"{Nome} excluído.");
        Atualizar();
        Novo();
    }

    [RelayCommand]
    private async Task EscolherImagem()
    {
        if (Principal.EscolherImagem is null) return;
        // As fotos dos produtos ficam numa pasta fixa do tablet (C:\Sistema_New\produtos).
        var arquivo = await Principal.EscolherImagem(Principal.Config.PastaFotos, true);
        if (arquivo is null) return;
        try
        {
            var relativo = Path.Combine("imagens", "produtos", Guid.NewGuid().ToString("N")[..12] + ".png");
            ImagemUtil.Importar(arquivo, Path.Combine(Sistema.PastaDados, relativo), 256);
            Imagem = relativo;
        }
        catch (Exception e)
        {
            Principal.MostrarAviso(e is ErroDeNegocio ? e.Message : "Não consegui abrir a imagem.", erro: true);
        }
    }

    [RelayCommand]
    private void RemoverImagem() => Imagem = null;

    private void Carregar(Produto p)
    {
        _carregando = true;
        Id = p.Id;
        Titulo = "Editar produto";
        Nome = p.Nome;
        Detalhe = p.Detalhe;
        Aba = Abas.FirstOrDefault(a => a.Id == p.AbaId) ?? Abas.FirstOrDefault();
        FichasPorUnidade = Math.Clamp(p.FichasPorUnidade, 1, 10);
        Preco = Dinheiro.ParaEdicao(p.PrecoCentavos);
        Custo = p.CustoCentavos == 0 ? "" : Dinheiro.ParaEdicao(p.CustoCentavos);
        ControlaEstoque = p.ControlaEstoque;
        Estoque = p.Estoque.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Ativo = p.Ativo;
        Cor = p.Cor;
        Imagem = p.Imagem;
        _carregando = false;
        AtualizarPosicoes(escolherLivre: false, atual: p.Posicao);
    }

    private void AtualizarPosicoes(bool escolherLivre, int? atual = null)
    {
        var anterior = Posicao;
        Posicoes.Clear();
        AvisoPosicao = "";
        if (Aba is null)
        {
            Posicao = null;
            return;
        }
        var livres = Sistema.Catalogo.PosicoesLivres(Aba.Id, TotalPosicoes, Id == 0 ? null : Id);
        foreach (var p in livres) Posicoes.Add(p);
        if (atual is { } a && !Posicoes.Contains(a))
        {
            // Posição fora da grade atual: mostra mesmo assim para não perder a informação.
            var i = 0;
            while (i < Posicoes.Count && Posicoes[i] < a) i++;
            Posicoes.Insert(i, a);
        }
        if (Posicoes.Count == 0)
            AvisoPosicao = $"A aba {Aba.Nome} está cheia ({TotalPosicoes} botões). Escolha outra aba ou aumente a " +
                           "grade em Configurações → Botões e abas.";

        var escolhida = atual ?? (escolherLivre || anterior is null || !Posicoes.Contains(anterior.Value)
            ? Posicoes.Cast<int?>().FirstOrDefault()
            : anterior);
        // O ComboBox perdeu a seleção ao limpar a lista: avisa de novo mesmo que o número seja o mesmo
        Posicao = null;
        Posicao = escolhida;
    }

    private void Atualizar()
    {
        var abas = Sistema.Catalogo.Abas().ToDictionary(a => a.Id, a => a.Nome);
        Lista.Clear();
        foreach (var p in Sistema.Catalogo.Produtos(Busca))
            Lista.Add(new ProdutoItem(p, abas.GetValueOrDefault(p.AbaId, "?")));
    }
}
