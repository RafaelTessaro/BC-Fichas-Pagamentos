using SkiaSharp;

namespace BCFichas.Core.Impressao;

public enum Alinhamento
{
    Esquerda,
    Centro,
    Direita,
}

/// <summary>
/// Monta uma tira de papel de cima para baixo (blocos de texto, linhas, imagens) e desenha num bitmap.
/// </summary>
internal sealed class Layout
{
    private readonly List<(int Altura, Action<SKCanvas, int> Desenhar)> _blocos = new();

    public Layout(int largura, int margem)
    {
        Largura = largura;
        Margem = margem;
    }

    public int Largura { get; }
    public int Margem { get; }
    public int LarguraUtil => Largura - 2 * Margem;

    public void Espaco(int altura) => _blocos.Add((altura, (_, _) => { }));

    public void Texto(string texto, SKTypeface fonte, float tamanho, Alinhamento alinhamento = Alinhamento.Centro,
        int maxLinhas = 3, bool invertido = false)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;
        using var medida = Pincel(fonte, tamanho);
        var linhas = Quebrar(texto, medida, LarguraUtil - (invertido ? 16 : 0), maxLinhas);
        AdicionarLinhas(linhas, fonte, tamanho, alinhamento, invertido);
    }

    /// <summary>Texto que diminui de tamanho até caber (nome do produto).</summary>
    public void TextoAjustado(string texto, SKTypeface fonte, float maximo, float minimo, int maxLinhas)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;
        var tamanho = maximo;
        List<string> linhas;
        while (true)
        {
            using var medida = Pincel(fonte, tamanho);
            linhas = Quebrar(texto, medida, LarguraUtil, int.MaxValue);
            var cabe = linhas.Count <= maxLinhas && linhas.All(l => medida.MeasureText(l) <= LarguraUtil);
            if (cabe || tamanho <= minimo)
            {
                if (!cabe) linhas = Quebrar(texto, medida, LarguraUtil, maxLinhas);
                break;
            }
            tamanho = Math.Max(minimo, tamanho - 4);
        }
        AdicionarLinhas(linhas, fonte, tamanho, Alinhamento.Centro, invertido: false, apertado: true);
    }

    /// <summary>Texto à esquerda e outro à direita na mesma linha.</summary>
    public void Par(string esquerda, string direita, SKTypeface fonte, float tamanho)
    {
        using var medida = Pincel(fonte, tamanho);
        var metricas = medida.FontMetrics;
        var altura = (int)Math.Ceiling(metricas.Descent - metricas.Ascent + 4);
        var larguraDireita = medida.MeasureText(direita);
        var esquerdaCortada = Cortar(esquerda, medida, LarguraUtil - larguraDireita - 12);
        _blocos.Add((altura, (canvas, y) =>
        {
            using var p = Pincel(fonte, tamanho);
            var baseline = y + 2 - p.FontMetrics.Ascent;
            canvas.DrawText(esquerdaCortada, Margem, baseline, p);
            canvas.DrawText(direita, Largura - Margem - p.MeasureText(direita), baseline, p);
        }));
    }

    public void Separador(bool tracejado = true, int espessura = 2)
    {
        _blocos.Add((espessura + 8, (canvas, y) =>
        {
            using var p = new SKPaint { Color = SKColors.Black, StrokeWidth = espessura, IsAntialias = false };
            if (tracejado) p.PathEffect = SKPathEffect.CreateDash([10, 8], 0);
            var meio = y + 4 + espessura / 2f;
            canvas.DrawLine(Margem, meio, Largura - Margem, meio, p);
        }));
    }

    public void Imagem(SKBitmap imagem)
    {
        _blocos.Add((imagem.Height, (canvas, y) =>
        {
            canvas.DrawBitmap(imagem, (Largura - imagem.Width) / 2f, y);
        }));
    }

    /// <summary>Faixa preta de lado a lado com texto branco.</summary>
    public void Faixa(string texto, SKTypeface fonte, float tamanho, int preenchimento = 8)
    {
        using var medida = Pincel(fonte, tamanho);
        var linhas = Quebrar(texto, medida, LarguraUtil, 2);
        var metricas = medida.FontMetrics;
        var alturaLinha = metricas.Descent - metricas.Ascent;
        var altura = (int)Math.Ceiling(alturaLinha * linhas.Count + preenchimento * 2);
        _blocos.Add((altura, (canvas, y) =>
        {
            using var fundo = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(0, y, Largura, altura, fundo);
            using var p = Pincel(fonte, tamanho, SKColors.White);
            var linhaY = y + preenchimento - p.FontMetrics.Ascent;
            foreach (var linha in linhas)
            {
                canvas.DrawText(linha, (Largura - p.MeasureText(linha)) / 2, linhaY, p);
                linhaY += alturaLinha;
            }
        }));
    }

    /// <summary>Texto branco numa caixa preta arredondada, centralizada.</summary>
    public void Selo(string texto, SKTypeface fonte, float tamanho)
    {
        using var medida = Pincel(fonte, tamanho);
        var metricas = medida.FontMetrics;
        var alturaTexto = metricas.Descent - metricas.Ascent;
        var larguraCaixa = Math.Min(LarguraUtil, medida.MeasureText(texto) + 48);
        var altura = (int)Math.Ceiling(alturaTexto + 16);
        _blocos.Add((altura, (canvas, y) =>
        {
            var x = (Largura - larguraCaixa) / 2;
            using var fundo = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(x, y, x + larguraCaixa, y + altura), 14, 14, fundo);
            using var p = Pincel(fonte, tamanho, SKColors.White);
            canvas.DrawText(texto, (Largura - p.MeasureText(texto)) / 2, y + 8 - p.FontMetrics.Ascent, p);
        }));
    }

    public void CodigoDeBarras(string codigo, int altura, SKTypeface fonteLegenda, float tamanhoLegenda)
    {
        var modulos = Code128.Modulos(codigo);
        var larguraModulo = Math.Max(1, Math.Min(3, LarguraUtil / modulos.Length));
        var larguraTotal = modulos.Length * larguraModulo;
        using var medida = Pincel(fonteLegenda, tamanhoLegenda);
        var alturaLegenda = (int)Math.Ceiling(medida.FontMetrics.Descent - medida.FontMetrics.Ascent);
        _blocos.Add((altura + alturaLegenda + 4, (canvas, y) =>
        {
            using var preto = new SKPaint { Color = SKColors.Black, IsAntialias = false };
            var x0 = (Largura - larguraTotal) / 2;
            for (var i = 0; i < modulos.Length; i++)
            {
                if (modulos[i])
                    canvas.DrawRect(x0 + i * larguraModulo, y, larguraModulo, altura, preto);
            }
            using var p = Pincel(fonteLegenda, tamanhoLegenda);
            canvas.DrawText(codigo, (Largura - p.MeasureText(codigo)) / 2, y + altura + 4 - p.FontMetrics.Ascent, p);
        }));
    }

    public SKBitmap Renderizar()
    {
        var altura = Math.Max(1, _blocos.Sum(b => b.Altura));
        var bitmap = new SKBitmap(new SKImageInfo(Largura, altura, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var y = 0;
        foreach (var (h, desenhar) in _blocos)
        {
            desenhar(canvas, y);
            y += h;
        }
        canvas.Flush();
        return bitmap;
    }

    private void AdicionarLinhas(List<string> linhas, SKTypeface fonte, float tamanho, Alinhamento alinhamento,
        bool invertido, bool apertado = false)
    {
        using var medida = Pincel(fonte, tamanho);
        var metricas = medida.FontMetrics;
        // Fontes grandes (nome do produto) ficam com as linhas mais juntas.
        var alturaLinha = apertado ? (metricas.Descent - metricas.Ascent) * 0.95f : medida.FontSpacing;
        var preenchimento = invertido ? 6 : 0;
        var altura = (int)Math.Ceiling(alturaLinha * linhas.Count + preenchimento * 2);

        _blocos.Add((altura, (canvas, y) =>
        {
            using var p = Pincel(fonte, tamanho, invertido ? SKColors.White : SKColors.Black);
            if (invertido)
            {
                using var fundo = new SKPaint { Color = SKColors.Black };
                canvas.DrawRect(Margem, y, LarguraUtil, altura, fundo);
            }
            var linhaY = y + preenchimento - p.FontMetrics.Ascent;
            foreach (var linha in linhas)
            {
                var largura = p.MeasureText(linha);
                var x = alinhamento switch
                {
                    Alinhamento.Esquerda => Margem + (invertido ? 8 : 0),
                    Alinhamento.Direita => Largura - Margem - largura - (invertido ? 8 : 0),
                    _ => (Largura - largura) / 2,
                };
                canvas.DrawText(linha, x, linhaY, p);
                linhaY += alturaLinha;
            }
        }));
    }

    internal static SKPaint Pincel(SKTypeface fonte, float tamanho, SKColor? cor = null) => new()
    {
        Typeface = fonte,
        TextSize = tamanho,
        IsAntialias = true,
        Color = cor ?? SKColors.Black,
        SubpixelText = true,
    };

    /// <summary>Quebra em linhas que caibam na largura; a última linha leva "…" se sobrar texto.</summary>
    internal static List<string> Quebrar(string texto, SKPaint pincel, float largura, int maxLinhas)
    {
        var palavras = texto.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var linhas = new List<string>();
        var atual = "";

        foreach (var palavra in palavras)
        {
            var tentativa = atual.Length == 0 ? palavra : atual + " " + palavra;
            if (pincel.MeasureText(tentativa) <= largura)
            {
                atual = tentativa;
                continue;
            }

            if (atual.Length > 0) linhas.Add(atual);
            atual = palavra;
            // Palavra sozinha maior que a linha: quebra por letra.
            while (pincel.MeasureText(atual) > largura && atual.Length > 1)
            {
                var corte = atual.Length - 1;
                while (corte > 1 && pincel.MeasureText(atual[..corte]) > largura) corte--;
                linhas.Add(atual[..corte]);
                atual = atual[corte..];
            }
        }
        if (atual.Length > 0) linhas.Add(atual);

        if (linhas.Count > maxLinhas)
        {
            var sobra = string.Join(" ", linhas.Skip(maxLinhas - 1));
            linhas = linhas.Take(maxLinhas - 1).ToList();
            linhas.Add(Cortar(sobra, pincel, largura));
        }
        return linhas;
    }

    internal static string Cortar(string texto, SKPaint pincel, float largura)
    {
        if (pincel.MeasureText(texto) <= largura) return texto;
        var t = texto;
        while (t.Length > 1 && pincel.MeasureText(t + "…") > largura) t = t[..^1];
        return t.TrimEnd() + "…";
    }
}
