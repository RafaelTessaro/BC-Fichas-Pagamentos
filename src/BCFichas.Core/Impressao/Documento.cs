using SkiaSharp;

namespace BCFichas.Core.Impressao;

/// <summary>Relatório simples (fechamento de caixa, comprovante de sangria, teste de impressão).</summary>
public sealed class Documento
{
    private readonly List<Action<Layout, float>> _partes = new();

    public Documento Titulo(string texto)
    {
        _partes.Add((l, s) => l.Texto(texto, Fontes.Negrito, 34 * s, maxLinhas: 2));
        return this;
    }

    public Documento Faixa(string texto)
    {
        _partes.Add((l, s) => l.Faixa(texto, Fontes.Negrito, 28 * s, (int)(8 * s)));
        return this;
    }

    public Documento Linha(string texto, Alinhamento alinhamento = Alinhamento.Esquerda, bool negrito = false,
        float tamanho = 24)
    {
        _partes.Add((l, s) => l.Texto(texto, negrito ? Fontes.Negrito : Fontes.Normal, tamanho * s, alinhamento, 4));
        return this;
    }

    public Documento Par(string esquerda, string direita, bool negrito = false, float tamanho = 24)
    {
        _partes.Add((l, s) => l.Par(esquerda, direita, negrito ? Fontes.Negrito : Fontes.Normal, tamanho * s));
        return this;
    }

    /// <summary>Como <see cref="Par"/>, mas só quando há o que mostrar à direita (ex.: operador de caixas antigos).</summary>
    public Documento ParSeHouver(string esquerda, string direita) =>
        string.IsNullOrWhiteSpace(direita) ? this : Par(esquerda, direita);

    public Documento Separador(bool tracejado = true)
    {
        _partes.Add((l, _) => l.Separador(tracejado));
        return this;
    }

    public Documento Espaco(int pontos = 12)
    {
        _partes.Add((l, s) => l.Espaco((int)(pontos * s)));
        return this;
    }

    public SKBitmap Renderizar(int largura)
    {
        var s = largura / 576f;
        var layout = new Layout(largura, (int)(16 * s));
        layout.Espaco((int)(10 * s));
        foreach (var parte in _partes) parte(layout, s);
        layout.Espaco((int)(16 * s));
        return layout.Renderizar();
    }
}
