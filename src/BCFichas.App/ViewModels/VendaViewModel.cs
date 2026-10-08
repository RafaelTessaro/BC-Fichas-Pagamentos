using System.Collections.ObjectModel;
using System.ComponentModel;
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
public sealed class BotaoProduto : ObservableObject
{
    public BotaoProduto(Produto? produto, Bitmap? imagem) => Preencher(produto, imagem);

    /// <summary>Tudo o que a tela mostra do botão (avisado de uma vez quando o botão passa a mostrar outro produto).</summary>
    private static readonly PropertyChangedEventArgs[] Mostrados =
    [
        new(nameof(Produto)), new(nameof(Vazio)), new(nameof(Disponivel)), new(nameof(Nome)), new(nameof(Detalhe)),
        new(nameof(TemDetalhe)), new(nameof(Preco)), new(nameof(Fundo)), new(nameof(Texto)), new(nameof(Imagem)),
        new(nameof(TemImagem)), new(nameof(Esgotado)), new(nameof(MostrarEstoque)), new(nameof(Estoque)),
        new(nameof(TamanhoNome)), new(nameof(TamanhoNomeFoto)),
    ];

    private void Preencher(Produto? produto, Bitmap? imagem)
    {
        Produto = produto;
        Imagem = imagem;
        if (produto is null)
        {
            Fundo = Brushes.Transparent;
            Texto = Brushes.White;
            return;
        }
        var cor = Recursos.CorOuPadrao(produto.Cor);
        Fundo = new SolidColorBrush(cor);
        Texto = Recursos.TextoSobre(cor);
    }

    /// <summary>
    /// Troca de aba: o mesmo botão (já montado na tela) passa a mostrar outro produto. Antes a grade inteira era
    /// desmontada e montada de novo a cada troca de aba.
    /// </summary>
    public void Mostrar(Produto? produto, Bitmap? imagem)
    {
        Preencher(produto, imagem);
        foreach (var e in Mostrados) OnPropertyChanged(e);
    }

    public Produto? Produto { get; private set; }
    public bool Vazio => Produto is null;
    public bool Disponivel => Produto is { Esgotado: false };
    public string Nome => Produto?.Nome ?? "";
    /// <summary>Detalhe do produto; no combo sem detalhe, quantas fichas ele imprime.</summary>
    public string Detalhe => Produto is null ? ""
        : Produto.Detalhe.Length > 0 ? Produto.Detalhe
        : Produto.EhCombo ? $"COMBO • {Produto.FichasPorVenda} FICHAS" : "";
    public bool TemDetalhe => Detalhe.Length > 0;
    public string Preco => Produto is null ? "" : Dinheiro.Formatar(Produto.PrecoCentavos);
    public IBrush Fundo { get; private set; } = Brushes.Transparent;
    public IBrush Texto { get; private set; } = Brushes.White;
    public Bitmap? Imagem { get; private set; }
    public bool TemImagem => Imagem is not null;
    public bool Esgotado => Produto?.Esgotado == true;
    public bool MostrarEstoque => Produto is { ControlaEstoque: true, Esgotado: false };
    public string Estoque => Produto is null ? "" : $"{Produto.Estoque} rest.";

    /// <summary>Letras menores quando o nome tem palavra comprida, para não quebrar no meio da palavra.</summary>
    public double TamanhoNome => MaiorPalavra switch { <= 8 => 21, <= 10 => 19, <= 12 => 17, _ => 15 };
    public double TamanhoNomeFoto => MaiorPalavra switch { <= 8 => 17, <= 10 => 15, <= 12 => 14, _ => 13 };

    private int MaiorPalavra =>
        Nome.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Length).DefaultIfEmpty(0).Max();

    /// <summary>Depois de uma venda: só o estoque muda, o botão continua o mesmo na tela (nada é refeito).</summary>
    public void AtualizarEstoque(Produto atual)
    {
        Produto = atual;
        OnPropertyChanged(nameof(Disponivel));
        OnPropertyChanged(nameof(Esgotado));
        OnPropertyChanged(nameof(MostrarEstoque));
        OnPropertyChanged(nameof(Estoque));
    }
}

/// <summary>Uma linha do pedido. Tocar em + ou − muda só os números dela (a linha não é refeita).</summary>
public sealed partial class LinhaItem : ObservableObject
{
    public LinhaItem(LinhaCarrinho linha)
    {
        ProdutoId = linha.Produto.Id;
        Atualizar(linha);
    }

    public long ProdutoId { get; }
    [ObservableProperty] private string _nome = "";
    [ObservableProperty] private string _unitario = "";
    [ObservableProperty] private int _quantidade;
    [ObservableProperty] private string _total = "";

    public void Atualizar(LinhaCarrinho linha)
    {
        Nome = linha.Produto.Nome;
        Unitario = Dinheiro.Formatar(linha.Produto.PrecoCentavos);
        Quantidade = linha.Quantidade;
        Total = Dinheiro.Formatar(linha.TotalCentavos);
    }
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

    /// <summary>
    /// O lugar do troco. Com itens no pedido, o troco some só na cor e o lugar fica: senão as linhas subiriam bem na
    /// hora de um toque, e o toque (que tira 1) cairia no item de baixo. O lugar some quando o pedido esvazia.
    /// </summary>
    [ObservableProperty] private bool _lugarDoTroco;

    /// <summary>Depois de uma venda em dinheiro com troco: mostra quanto devolver ao cliente.</summary>
    public void MostrarUltimoTroco(long troco, long recebido)
    {
        if (troco <= 0) return;
        UltimoTroco = Dinheiro.Formatar(troco);
        UltimoRecebido = $"Recebido {Dinheiro.Formatar(recebido)}";
        MostrarTroco = true;
        LugarDoTroco = true;
        _timerTroco.Stop();
        _timerTroco.Start();
    }

    public void EsconderTroco()
    {
        _timerTroco.Stop();
        MostrarTroco = false;
        if (Vazio) LugarDoTroco = false;
    }

    partial void OnVazioChanged(bool value)
    {
        if (value && !MostrarTroco) LugarDoTroco = false;
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

    /// <summary>
    /// Depois de uma venda: atualiza o "restam X" e o "esgotado" nos botões que já estão na tela (refazer a grade
    /// inteira travava o tablet quase 1 segundo a cada venda).
    /// </summary>
    public void AtualizarEstoque()
    {
        var aba = Abas.FirstOrDefault(a => a.Ativa);
        if (aba is null) return;
        var produtos = ProdutosDaTela(aba.Aba);
        if (produtos.Count == Botoes.Count && produtos.Select(p => p.Id).SequenceEqual(Botoes.Select(b => b.Produto?.Id ?? 0)))
            for (var i = 0; i < produtos.Count; i++) Botoes[i].AtualizarEstoque(produtos[i]);
        else
            MontarBotoes(aba.Aba);
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
        // Com uma janela aberta (ex.: Enter no teclado com o botão ainda selecionado) não abre outra por baixo
        if (_principal.Sessao is null || _principal.TemDialogo) return;
        _principal.AbrirDialogo(new PagamentoViewModel(_principal, _principal.Sessao, _carrinho.Linhas.ToList(),
            aoConcluir: LimparPedido));
    }

    [RelayCommand]
    private void AbrirMenu()
    {
        if (!_principal.TemDialogo) _principal.AbrirDialogo(new MenuViewModel(_principal));
    }

    /// <summary>Barra lateral: sangria e suprimento (antes ficava no menu).</summary>
    [RelayCommand]
    private void AbrirSangria()
    {
        if (!_principal.TemDialogo)
            _principal.AbrirProtegido(TelaProtegida.Sangria, () => new SangriaViewModel(_principal));
    }

    /// <summary>Barra lateral: fechar o caixa (no modo teste, sair do teste).</summary>
    [RelayCommand]
    private void FecharCaixa()
    {
        if (_principal.TemDialogo) return;
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
        var produtos = ProdutosDaTela(aba);
        // Os botões que já estão na tela passam a mostrar os produtos desta aba; só os que sobram ou faltam saem ou
        // entram. Refazer a grade inteira (botão, foto, textos e estilos de cada um) pesava a cada troca de aba.
        while (Botoes.Count > produtos.Count) Botoes.RemoveAt(Botoes.Count - 1);
        for (var i = 0; i < produtos.Count; i++)
        {
            var imagem = CacheImagens.Obter(_principal.Sistema.Impressao.CaminhoImagem(produtos[i].Imagem));
            if (i < Botoes.Count) Botoes[i].Mostrar(produtos[i], imagem);
            else Botoes.Add(new BotaoProduto(produtos[i], imagem));
        }
        SemProdutos = Botoes.Count == 0;
    }

    private List<Produto> ProdutosDaTela(Aba aba) =>
        _principal.Sistema.Catalogo.ProdutosDaAba(aba.Id)
            .OrderBy(p => p.Posicao).ThenBy(p => p.Nome)
            .Take(aba.Capacidade)
            .ToList();

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
        // Só a linha que mudou muda na tela (refazer todas a cada toque pesava no tablet)
        var atuais = _carrinho.Linhas.Where(l => l.Quantidade > 0).ToList();
        for (var i = Linhas.Count - 1; i >= 0; i--)
            if (atuais.All(l => l.Produto.Id != Linhas[i].ProdutoId)) Linhas.RemoveAt(i);
        for (var i = 0; i < atuais.Count; i++)
        {
            var existente = -1;
            for (var j = 0; j < Linhas.Count; j++)
                if (Linhas[j].ProdutoId == atuais[i].Produto.Id) existente = j;
            if (existente < 0)
            {
                Linhas.Insert(i, new LinhaItem(atuais[i]));
                continue;
            }
            Linhas[existente].Atualizar(atuais[i]);
            if (existente != i) Linhas.Move(existente, i);
        }
        Total = Dinheiro.Formatar(_carrinho.TotalCentavos);
        var qtd = _carrinho.QuantidadeItens;
        QuantidadeTexto = qtd == 0 ? "" : qtd == 1 ? "1 item" : $"{qtd} itens";
        Vazio = qtd == 0;
    }
}
