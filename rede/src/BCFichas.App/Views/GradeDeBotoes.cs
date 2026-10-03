using Avalonia;
using Avalonia.Controls;

namespace BCFichas.App.Views;

/// <summary>
/// Onde fica cada botão de produto: retângulos dentro da área da tela de venda, sem espaço vazio.
/// </summary>
public sealed record ArranjoDaGrade(int Colunas, IReadOnlyList<int> PorLinha, IReadOnlyList<Rect> Botoes)
{
    public int Linhas => PorLinha.Count;
}

/// <summary>
/// Painel dos botões de produto. Os botões ocupam a área toda, na ordem das posições, sem deixar espaço vazio:
/// <list type="bullet">
/// <item><b>Automático</b> (colunas = 0): testa de 1 a 6 colunas e fica com a que dá o maior botão, com as linhas
/// equilibradas (7 produtos = 4 + 3) — o mesmo jeito das galerias de vídeo-chamada (Jitsi) e das galerias
/// "justificadas", em que a linha mais curta estica para ocupar a largura.</item>
/// <item><b>Colunas × linhas</b> da aba: as posições não mudam (a linha de cima é sempre cheia); com menos produtos,
/// as linhas que sobram somem (os botões ficam mais altos) e a última linha estica.</item>
/// </list>
/// Os botões sempre ocupam a área toda. No automático, o formato (largura entre 0,6 e 1,8 da altura, como
/// recomendam as galerias de vídeo-chamada) só serve para escolher a melhor arrumação.
/// </summary>
public sealed class GradeDeBotoes : Panel
{
    public const int MaximoColunasAutomatico = 6;
    private const double LarguraMinima = 0.6;   // largura ÷ altura
    private const double LarguraMaxima = 1.8;
    private const double EsticadaMaxima = 1.6;  // botão da linha curta ÷ botão da linha cheia (automático)
    private const double PesoAltura = 1.2;      // prefere botões um pouco mais largos que altos

    public static readonly StyledProperty<int> ColunasProperty =
        AvaloniaProperty.Register<GradeDeBotoes, int>(nameof(Colunas));

    public static readonly StyledProperty<int> LinhasProperty =
        AvaloniaProperty.Register<GradeDeBotoes, int>(nameof(Linhas));

    static GradeDeBotoes() => AffectsMeasure<GradeDeBotoes>(ColunasProperty, LinhasProperty);

    /// <summary>Botões por linha; 0 = automático.</summary>
    public int Colunas
    {
        get => GetValue(ColunasProperty);
        set => SetValue(ColunasProperty, value);
    }

    /// <summary>Linhas da grade quando as colunas são escolhidas (com menos produtos, sobram e somem).</summary>
    public int Linhas
    {
        get => GetValue(LinhasProperty);
        set => SetValue(LinhasProperty, value);
    }

    protected override Size MeasureOverride(Size disponivel)
    {
        var tamanho = new Size(double.IsInfinity(disponivel.Width) ? 800 : disponivel.Width,
            double.IsInfinity(disponivel.Height) ? 600 : disponivel.Height);
        var arranjo = Calcular(Children.Count, tamanho.Width, tamanho.Height, Colunas, Linhas);
        for (var i = 0; i < Children.Count; i++)
            Children[i].Measure(i < arranjo.Botoes.Count ? arranjo.Botoes[i].Size : default);
        return tamanho;
    }

    protected override Size ArrangeOverride(Size final)
    {
        var arranjo = Calcular(Children.Count, final.Width, final.Height, Colunas, Linhas);
        for (var i = 0; i < Children.Count; i++)
            Children[i].Arrange(i < arranjo.Botoes.Count ? arranjo.Botoes[i] : default);
        return final;
    }

    /// <summary>
    /// Retângulo de cada um dos <paramref name="quantidade"/> botões numa área de
    /// <paramref name="largura"/> × <paramref name="altura"/>. Com colunas × linhas, mostra no máximo colunas × linhas.
    /// </summary>
    public static ArranjoDaGrade Calcular(int quantidade, double largura, double altura, int colunas = 0, int linhas = 0)
    {
        if (quantidade <= 0 || largura <= 0 || altura <= 0) return new ArranjoDaGrade(0, [], []);
        return colunas > 0
            ? Fixo(quantidade, largura, altura, colunas, Math.Max(1, linhas))
            : Automatico(quantidade, largura, altura);
    }

    private static ArranjoDaGrade Automatico(int n, double largura, double altura)
    {
        ArranjoDaGrade? melhor = null;
        var melhorNota = double.MinValue;
        for (var k = 1; k <= Math.Min(n, MaximoColunasAutomatico); k++)
        {
            var linhas = (int)Math.Ceiling(n / (double)k);
            var porLinha = Equilibrar(n, linhas);
            var cheia = porLinha[0];
            if (cheia != k) continue; // dá o mesmo arranjo de uma quantidade menor de colunas
            var curta = porLinha[^1];
            var h = altura / linhas;
            var w = largura / cheia;
            var wCurta = largura / curta;
            if (linhas > 1 && wCurta / w > EsticadaMaxima) continue;
            h = Math.Min(h, w / LarguraMinima);
            w = Math.Min(w, h * LarguraMaxima);
            var nota = Math.Min(w, h * PesoAltura);
            // Empate: fica com menos colunas (botões maiores na vertical, mais fáceis de ler)
            if (nota > melhorNota + 0.5)
            {
                melhorNota = nota;
                melhor = Montar(porLinha, cheia, largura, altura);
            }
        }
        // Sempre há uma opção (com 1 coluna nada estica)
        return melhor ?? Montar(Equilibrar(n, n), 1, largura, altura);
    }

    private static ArranjoDaGrade Fixo(int n, double largura, double altura, int colunas, int linhas)
    {
        n = Math.Min(n, colunas * linhas);
        var usadas = (int)Math.Ceiling(n / (double)colunas);
        // As linhas de cima são sempre cheias: cada produto fica no mesmo lugar quando outro entra ou sai.
        var porLinha = new List<int>();
        for (var i = 0; i < usadas; i++) porLinha.Add(Math.Min(colunas, n - i * colunas));
        return Montar(porLinha, colunas, largura, altura);
    }

    /// <summary>n produtos em <paramref name="linhas"/> linhas, as de cima com um a mais (7 em 2 = 4 + 3).</summary>
    private static List<int> Equilibrar(int n, int linhas)
    {
        var porLinha = new List<int>();
        for (var i = 0; i < linhas; i++) porLinha.Add(n / linhas + (i < n % linhas ? 1 : 0));
        return porLinha;
    }

    /// <summary>Linhas da mesma altura; em cada uma os botões dividem a largura toda. Nada fica vazio.</summary>
    private static ArranjoDaGrade Montar(IReadOnlyList<int> porLinha, int colunas, double largura, double altura)
    {
        var h = altura / porLinha.Count;
        var botoes = new List<Rect>();
        for (var i = 0; i < porLinha.Count; i++)
        {
            var w = largura / porLinha[i];
            for (var j = 0; j < porLinha[i]; j++) botoes.Add(new Rect(j * w, i * h, w, h));
        }
        return new ArranjoDaGrade(colunas, porLinha.ToList(), botoes);
    }
}
