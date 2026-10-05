using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;

namespace BCFichas.App.ViewModels;

/// <summary>
/// Como os botões de uma aba se arrumam na tela de venda: automático (os produtos dividem a tela, sem espaço vazio)
/// ou colunas × linhas escolhidas. Mostra a prévia com os produtos da aba. Vale ao tocar em Salvar nas
/// Configurações (como o resto das abas).
/// </summary>
public sealed partial class GradeAbaViewModel : ViewModelBase
{
    private readonly PrincipalViewModel _p;
    private readonly AbaEdicao _aba;
    private readonly List<BotaoProduto> _produtos;

    public GradeAbaViewModel(PrincipalViewModel principal, AbaEdicao aba, IReadOnlyList<Produto> produtos)
    {
        _p = principal;
        _aba = aba;
        _produtos = produtos.Select(p => new BotaoProduto(p, null)).ToList();
        Titulo = aba.NomeLimpo.Length > 0 ? $"Botões da aba {aba.NomeLimpo}" : "Botões da aba nova";
        Subtitulo = produtos.Count switch
        {
            0 => "Ainda sem produtos.",
            1 => "1 produto nesta aba.",
            _ => $"{produtos.Count} produtos nesta aba.",
        };
        _automatica = aba.Automatica;
        _colunas = aba.Automatica ? Math.Min(4, Math.Max(1, produtos.Count)) : aba.Colunas;
        _linhas = aba.Automatica ? Math.Max(1, (int)Math.Ceiling(Math.Max(1, produtos.Count) / (double)_colunas)) : aba.Linhas;
        Atualizar();
    }

    public string Titulo { get; }
    public string Subtitulo { get; }
    public List<int> OpcoesColunas { get; } = Enumerable.Range(1, Core.Servicos.CatalogoServico.MaximoColunas).ToList();
    public List<int> OpcoesLinhas { get; } = Enumerable.Range(1, Core.Servicos.CatalogoServico.MaximoLinhas).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Fixa))]
    private bool _automatica;

    [ObservableProperty] private int _colunas;
    [ObservableProperty] private int _linhas;

    public bool Fixa => !Automatica;

    // Prévia: os produtos da aba como ficam na tela de venda
    public ObservableCollection<BotaoProduto> Previa { get; } = new();
    [ObservableProperty] private int _colunasPrevia;
    [ObservableProperty] private int _linhasPrevia;
    [ObservableProperty] private string _explicacao = "";
    [ObservableProperty] private string _aviso = "";
    public bool SemProdutos => _produtos.Count == 0;
    public string Capacidade => $"Até {Colunas * Linhas} botões nesta aba";

    partial void OnAutomaticaChanged(bool value) => Atualizar();
    partial void OnColunasChanged(int value) => Atualizar();
    partial void OnLinhasChanged(int value) => Atualizar();

    private void Atualizar()
    {
        var n = _produtos.Count;
        var cabem = Automatica ? Aba.MaximoAutomatico : Colunas * Linhas;
        ColunasPrevia = Automatica ? 0 : Colunas;
        LinhasPrevia = Automatica ? 0 : Linhas;
        Previa.Clear();
        foreach (var b in _produtos.Take(cabem)) Previa.Add(b);
        OnPropertyChanged(nameof(Capacidade));

        Explicacao = Automatica
            ? "O programa escolhe quantos botões vão em cada linha para eles ficarem do maior tamanho possível. " +
              "Os produtos dividem a tela toda, sem espaço vazio, e a arrumação muda sozinha quando você cadastra " +
              "ou tira um produto."
            : $"{Colunas} botões por linha, na ordem das posições. Com menos de {cabem} produtos, as linhas que " +
              "sobram somem e os botões crescem; a última linha estica para não deixar espaço vazio.";
        Aviso = !Automatica && n > cabem
            ? $"Esta aba tem {n} produtos e só cabem {cabem}: {n - cabem} ficariam fora da tela. Aumente as linhas " +
              "ou as colunas."
            : "";
    }

    [RelayCommand]
    private void EscolherAutomatica() => Automatica = true;

    [RelayCommand]
    private void EscolherFixa() => Automatica = false;

    [RelayCommand]
    private void Aplicar()
    {
        _aba.Colunas = Automatica ? 0 : Colunas;
        _aba.Linhas = Automatica ? 0 : Linhas;
        _p.FecharDialogo(this);
    }

    [RelayCommand]
    private void Cancelar() => _p.FecharDialogo(this);
}
