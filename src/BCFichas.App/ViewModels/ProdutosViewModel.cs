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
    public string Info { get; } = (p.Ativo ? $"{aba} • posição {p.Posicao}" : $"{aba} • fora da tela de venda") +
                                  (p.EhCombo ? $" • COMBO ({p.FichasPorVenda} fichas)" : "");
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

/// <summary>Uma linha das fichas do combo: o que sai escrito, quantas fichas e o valor de cada uma.</summary>
public sealed partial class ComponenteEdicao : ObservableObject
{
    private readonly Action _aoMudar;
    private readonly Action<ComponenteEdicao> _remover;

    public ComponenteEdicao(long? produtoId, string nome, string detalhe, int quantidade, long valor, Action aoMudar,
        Action<ComponenteEdicao> remover)
    {
        ProdutoId = produtoId;
        Nome = nome;
        Detalhe = detalhe;
        _quantidade = quantidade;
        ValorCentavos = valor;
        _aoMudar = aoMudar;
        _remover = remover;
    }

    /// <summary>Produto cadastrado ligado à ficha (o estoque dele baixa junto); nulo no texto livre.</summary>
    public long? ProdutoId { get; }
    public string Nome { get; }
    public string Detalhe { get; }
    public long ValorCentavos { get; }
    public string Valor => Dinheiro.Formatar(ValorCentavos);
    public bool Ligado => ProdutoId is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Total))]
    private int _quantidade;

    public long TotalCentavos => Quantidade * ValorCentavos;
    public string Total => Dinheiro.Formatar(TotalCentavos);

    partial void OnQuantidadeChanged(int value) => _aoMudar();

    [RelayCommand]
    private void Mais()
    {
        if (Quantidade < 200) Quantidade++;
    }

    /// <summary>Tira uma ficha; tirando a última, a linha sai do combo.</summary>
    [RelayCommand]
    private void Menos()
    {
        if (Quantidade > 1) Quantidade--;
        else _remover(this);
    }

    /// <summary>Tira todas as fichas desta linha.</summary>
    [RelayCommand]
    private void Remover() => _remover(this);
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
    public ObservableCollection<ComponenteEdicao> Componentes { get; } = new();
    /// <summary>Produtos cadastrados que dá para pôr nas fichas do combo (não pode combo dentro de combo).</summary>
    public ObservableCollection<Produto> ParaOCombo { get; } = new();
    /// <summary>Atalhos de vales: um toque preenche "VALE R$ 10,00" e o valor.</summary>
    public List<long> Vales { get; } = [100, 200, 500, 1000, 2000, 5000];

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
    [ObservableProperty] private bool _ehCombo;
    [ObservableProperty] private string _resumoCombo = "";
    [ObservableProperty] private string _totalFichas = Dinheiro.Formatar(0);
    [ObservableProperty] private string _quantasFichas = "";
    [ObservableProperty] private string _confereCombo = "";
    [ObservableProperty] private bool _comboConfere;

    /// <summary>Tela "Fichas do combo" aberta por cima do cadastro.</summary>
    [ObservableProperty] private bool _editandoCombo;

    // Linha nova das fichas do combo (como no sistema antigo: produto, detalhe, quantidade e valor)
    [ObservableProperty] private string _fichaNome = "";
    [ObservableProperty] private string _fichaDetalhe = "";
    [ObservableProperty] private string _fichaQuantidade = "1";
    [ObservableProperty] private string _fichaValor = "";

    public bool Editando => Id != 0;
    public bool NaoEhCombo => !EhCombo;
    public bool TemFichasNoCombo => Componentes.Count > 0;
    public bool SemFichasNoCombo => Componentes.Count == 0;
    public string NomeDoCombo => string.IsNullOrWhiteSpace(Nome) ? "Combo novo" : Nome.Trim().ToUpperInvariant();
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

    partial void OnEhComboChanged(bool value)
    {
        OnPropertyChanged(nameof(NaoEhCombo));
        AtualizarResumoCombo();
        // Ligou "Combo" num produto sem fichas: já abre a tela para montar as fichas.
        if (value && !_carregando && Componentes.Count == 0) AbrirCombo();
    }

    partial void OnPrecoChanged(string value) => AtualizarResumoCombo();

    // Prévia do botão: cor marcada e foto (sumiram por engano na versão 3.4)
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
    partial void OnNomeChanged(string value) => OnPropertyChanged(nameof(NomeDoCombo));

    partial void OnAtivoChanged(bool value)
    {
        if (!_carregando && value && Posicao is null) AtualizarPosicoes(escolherLivre: true);
    }

    [RelayCommand]
    private void AbrirCombo()
    {
        EhCombo = true;
        EditandoCombo = true;
    }

    [RelayCommand]
    private void FecharCombo()
    {
        EditandoCombo = false;
        if (Componentes.Count == 0) EhCombo = false;
    }

    /// <summary>Fecha a tela das fichas e já grava o produto.</summary>
    [RelayCommand]
    private void SalvarCombo()
    {
        if (Componentes.Count == 0)
        {
            Principal.MostrarAviso("Adicione as fichas do combo: produto, quantidade e valor, e toque em Adicionar.", erro: true);
            return;
        }
        EditandoCombo = false;
        Salvar();
    }

    /// <summary>
    /// Põe a linha digitada nas fichas. Se já existe a mesma ficha (nome, detalhe e valor), soma a quantidade,
    /// como no sistema antigo. Depois limpa o produto, a quantidade e o valor e mantém o detalhe (ex.: validade).
    /// </summary>
    [RelayCommand]
    private void AdicionarFicha()
    {
        var nome = FichaNome.Trim().ToUpperInvariant();
        var detalhe = FichaDetalhe.Trim().ToUpperInvariant();
        if (nome.Length == 0)
        {
            Principal.MostrarAviso("Escreva o que sai na ficha (ex.: VALE R$ 10,00).", erro: true);
            return;
        }
        var quantidade = 1;
        if (!string.IsNullOrWhiteSpace(FichaQuantidade) &&
            (!int.TryParse(FichaQuantidade.Trim(), out quantidade) || quantidade is < 1 or > 200))
        {
            Principal.MostrarAviso("A quantidade de fichas vai de 1 a 200.", erro: true);
            return;
        }
        if (!Dinheiro.TentarLer(FichaValor, out var valor) || valor < 0)
        {
            Principal.MostrarAviso("Digite o valor de cada ficha, por exemplo 10,00.", erro: true);
            return;
        }

        var igual = Componentes.FirstOrDefault(c => c.Nome == nome && c.Detalhe == detalhe && c.ValorCentavos == valor);
        if (igual is not null)
            igual.Quantidade = Math.Min(200, igual.Quantidade + quantidade);
        else
            Componentes.Add(new ComponenteEdicao(null, nome, detalhe, quantidade, valor, AtualizarResumoCombo, RemoverFicha));

        FichaNome = "";
        FichaQuantidade = "1";
        FichaValor = "";
        AtualizarResumoCombo();
    }

    /// <summary>Atalho de vale: um toque põe "VALE R$ 10,00" de R$ 10,00 no combo (com a Qtde e o Detalhe digitados).</summary>
    [RelayCommand]
    private void AdicionarVale(long centavos) => PorAtalho("VALE " + Dinheiro.Formatar(centavos), centavos, null);

    /// <summary>Atalho de produto cadastrado: um toque põe a ficha dele no combo (e liga ao estoque dele).</summary>
    [RelayCommand]
    private void AdicionarProduto(Produto produto) =>
        PorAtalho(produto.Nome, produto.PrecoCentavos, produto.Id, produto.Detalhe);

    private void PorAtalho(string nome, long valor, long? produtoId, string detalheDoProduto = "")
    {
        var quantidade = int.TryParse(FichaQuantidade.Trim(), out var q) && q is >= 1 and <= 200 ? q : 1;
        var detalhe = FichaDetalhe.Trim().ToUpperInvariant();
        if (detalhe.Length == 0) detalhe = detalheDoProduto;
        var igual = Componentes.FirstOrDefault(c => c.Nome == nome && c.Detalhe == detalhe && c.ValorCentavos == valor);
        if (igual is not null)
            igual.Quantidade = Math.Min(200, igual.Quantidade + quantidade);
        else
            Componentes.Add(new ComponenteEdicao(produtoId, nome, detalhe, quantidade, valor, AtualizarResumoCombo, RemoverFicha));
        FichaQuantidade = "1";
        AtualizarResumoCombo();
    }

    [RelayCommand]
    private void LimparFichas()
    {
        Componentes.Clear();
        AtualizarResumoCombo();
    }

    /// <summary>Preço do combo = soma das fichas (ex.: vales que somam R$ 100).</summary>
    [RelayCommand]
    private void UsarTotalComoPreco() =>
        Preco = Dinheiro.ParaEdicao(Componentes.Sum(c => c.TotalCentavos));

    private void RemoverFicha(ComponenteEdicao componente)
    {
        Componentes.Remove(componente);
        AtualizarResumoCombo();
    }

    /// <summary>"26 fichas • as fichas somam R$ 100,00 • preço do combo R$ 100,00 (igual às fichas)".</summary>
    private void AtualizarResumoCombo()
    {
        OnPropertyChanged(nameof(TemFichasNoCombo));
        OnPropertyChanged(nameof(SemFichasNoCombo));
        var fichas = Componentes.Sum(c => c.Quantidade);
        var soma = Componentes.Sum(c => c.TotalCentavos);
        TotalFichas = Dinheiro.Formatar(soma);
        QuantasFichas = fichas == 1 ? "1 ficha" : $"{fichas} fichas";

        if (Dinheiro.TentarLer(Preco, out var preco))
        {
            ComboConfere = preco == soma;
            ConfereCombo = preco == soma ? $"Preço do combo: {Dinheiro.Formatar(preco)} — igual às fichas"
                : preco < soma ? $"Preço do combo: {Dinheiro.Formatar(preco)} — {Dinheiro.Formatar(soma - preco)} de desconto"
                : $"Preço do combo: {Dinheiro.Formatar(preco)} — {Dinheiro.Formatar(preco - soma)} a mais que as fichas";
        }
        else
        {
            ComboConfere = false;
            ConfereCombo = "Preço do combo ainda não definido";
        }

        ResumoCombo = !EhCombo ? ""
            : fichas == 0 ? "Nenhuma ficha ainda: toque em Montar as fichas."
            : $"{QuantasFichas} • as fichas somam {TotalFichas} • " +
              string.Join(", ", Componentes.Take(4).Select(c => $"{c.Quantidade} × {c.Nome}")) +
              (Componentes.Count > 4 ? "…" : "");
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
        Componentes.Clear();
        EhCombo = false;
        EditandoCombo = false;
        FichaNome = FichaDetalhe = FichaValor = "";
        FichaQuantidade = "1";
        CarregarParaOCombo();
        _carregando = false;
        AtualizarPosicoes(escolherLivre: true);
        AtualizarResumoCombo();
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
        // Produto escondido da tela de venda (ex.: vale usado só em combos) não precisa de posição.
        if (Ativo && Posicao is null)
        {
            Principal.MostrarAviso(AvisoPosicao.Length > 0 ? AvisoPosicao : "Escolha a posição do botão na tela.", erro: true);
            return;
        }
        var componentes = new List<ComponenteCombo>();
        if (EhCombo)
        {
            if (Componentes.Count == 0)
            {
                Principal.MostrarAviso("Combo sem fichas: toque em Montar as fichas e adicione o que sai em cada ficha.", erro: true);
                return;
            }
            componentes.AddRange(Componentes.Select(c => new ComponenteCombo
            {
                ProdutoId = c.ProdutoId,
                Nome = c.Nome,
                Detalhe = c.Detalhe,
                Quantidade = c.Quantidade,
                ValorCentavos = c.ValorCentavos,
            }));
        }

        try
        {
            var produto = Sistema.Catalogo.SalvarProduto(new Produto
            {
                Id = Id,
                Nome = Nome,
                Detalhe = Detalhe,
                AbaId = Aba?.Id ?? 0,
                Posicao = Posicao ?? 0,
                FichasPorUnidade = EhCombo ? 1 : FichasPorUnidade,
                Componentes = componentes,
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
        Componentes.Clear();
        foreach (var c in p.Componentes)
            Componentes.Add(new ComponenteEdicao(c.ProdutoId, c.Nome, c.Detalhe, c.Quantidade, c.ValorCentavos,
                AtualizarResumoCombo, RemoverFicha));
        EhCombo = p.EhCombo;
        EditandoCombo = false;
        CarregarParaOCombo();
        _carregando = false;
        AtualizarPosicoes(escolherLivre: false, atual: p.Ativo && p.Posicao > 0 ? p.Posicao : null);
        AtualizarResumoCombo();
    }

    private void CarregarParaOCombo()
    {
        ParaOCombo.Clear();
        foreach (var p in Sistema.Catalogo.Produtos().Where(p => !p.EhCombo && p.Id != Id).OrderBy(p => p.Nome))
            ParaOCombo.Add(p);
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
