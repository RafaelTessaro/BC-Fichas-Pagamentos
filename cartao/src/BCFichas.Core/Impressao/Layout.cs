using SkiaSharp;

namespace BCFichas.Core.Impressao;

public enum Alinhamento
{
    Esquerda,
    Centro,
    Direita,
}

/// <summary>Uma linha de texto dentro de <see cref="Layout.Coluna"/>.</summary>
/// <param name="Direita">Texto opcional encostado à direita na mesma linha.</param>
/// <param name="Tarja">Texto branco numa tarja preta (valor em destaque).</param>
internal sealed record Trecho(string Texto, SKTypeface Fonte, float Tamanho,
    Alinhamento Alinhamento = Alinhamento.Esquerda, string? Direita = null, bool Tarja = false);

/// <summary>
/// Monta uma tira de papel de cima para baixo (blocos de texto, linhas, imagens) e desenha num bitmap.
/// </summary>
internal sealed class Layout
{
    private readonly List<(int Altura, Action<SKCanvas, int> Desenhar)> _blocos = new();

    /// <summary>Espessura da moldura, contada da beirada do papel (traço de 3 pontos centrado em 4,5).</summary>
    public const int LarguraBorda = 6;

    /// <param name="recuo">Com moldura, quanto as faixas de lado a lado ficam afastadas da beirada.</param>
    public Layout(int largura, int margem, int recuo = 0)
    {
        Largura = largura;
        Margem = margem;
        Recuo = recuo;
    }

    public int Largura { get; }
    public int Margem { get; }
    public int Recuo { get; }
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

    /// <summary>
    /// Texto que diminui de tamanho até caber (nome do produto). Prefere uma linha só, ocupando a largura;
    /// quando numa linha ficaria pequeno demais, quebra entre as palavras (nunca no meio de uma palavra).
    /// </summary>
    public void TextoAjustado(string texto, SKTypeface fonte, float maximo, float minimo, int maxLinhas)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;
        texto = string.Join(' ', texto.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // Uma linha: o tamanho que enche a largura (a largura do texto cresce junto com a letra).
        float umaLinha;
        using (var medida = Pincel(fonte, maximo))
            umaLinha = Math.Min(maximo, (float)Math.Floor(maximo * LarguraUtil / Math.Max(1, medida.MeasureText(texto))));
        if (maxLinhas <= 1 || umaLinha >= maximo * 0.6f || !texto.Contains(' '))
        {
            var tamanhoUnico = Math.Max(minimo, umaLinha);
            using var medida = Pincel(fonte, tamanhoUnico);
            AdicionarLinhas([Cortar(texto, medida, LarguraUtil)], fonte, tamanhoUnico, Alinhamento.Centro,
                invertido: false, apertado: true);
            return;
        }

        // Várias linhas, um pouco menores que o máximo para a ficha não ficar comprida demais.
        var tamanho = maximo * 0.8f;
        List<string> linhas;
        while (true)
        {
            using var medida = Pincel(fonte, tamanho);
            linhas = Quebrar(texto, medida, LarguraUtil, int.MaxValue);
            var maiorPalavra = texto.Split(' ').Max(p => medida.MeasureText(p));
            var cabe = linhas.Count <= maxLinhas && maiorPalavra <= LarguraUtil;
            if (cabe || tamanho <= minimo)
            {
                if (!cabe) linhas = Quebrar(texto, medida, LarguraUtil, maxLinhas);
                break;
            }
            tamanho = Math.Max(minimo, tamanho - 2);
        }
        // Se em duas linhas a letra não fica maior que em uma, fica em uma só.
        if (tamanho <= umaLinha)
        {
            using var medida = Pincel(fonte, umaLinha);
            linhas = [Cortar(texto, medida, LarguraUtil)];
            tamanho = Math.Max(minimo, umaLinha);
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

    /// <summary>
    /// Tracejado dos separadores: criado uma vez só (antes cada separador criava um e não soltava, e ele ficava na
    /// memória do Skia até o coletor passar).
    /// </summary>
    private static readonly SKPathEffect Tracejado = SKPathEffect.CreateDash([10, 8], 0);

    public void Separador(bool tracejado = true, int espessura = 2)
    {
        _blocos.Add((espessura + 8, (canvas, y) =>
        {
            using var p = new SKPaint { Color = SKColors.Black, StrokeWidth = espessura, IsAntialias = false };
            if (tracejado) p.PathEffect = Tracejado;
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

    /// <summary>Faixa preta de lado a lado (dentro da moldura, se houver) com texto branco.</summary>
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
            canvas.DrawRect(Recuo, y, Largura - 2 * Recuo, altura, fundo);
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

    /// <summary>
    /// Linhas de texto com uma imagem (logo) ao lado. O bloco fica com a altura do maior dos dois
    /// e ambos ficam centralizados na vertical.
    /// </summary>
    public void Coluna(IReadOnlyList<Trecho> trechos, SKBitmap? imagem = null, bool imagemADireita = true, int espaco = 14)
    {
        if (trechos.Count == 0 && imagem is null) return;
        var larguraImagem = imagem?.Width ?? 0;
        var xTexto = Margem + (imagem is not null && !imagemADireita ? larguraImagem + espaco : 0);
        var larguraTexto = LarguraUtil - (imagem is null ? 0 : larguraImagem + espaco);
        var alturas = trechos.Select(AlturaTrecho).ToList();
        var alturaTexto = alturas.Sum();
        var altura = (int)Math.Ceiling(Math.Max(alturaTexto, imagem?.Height ?? 0));

        _blocos.Add((altura, (canvas, y) =>
        {
            if (imagem is not null)
            {
                var xi = imagemADireita ? Largura - Margem - larguraImagem : Margem;
                canvas.DrawBitmap(imagem, xi, y + (altura - imagem.Height) / 2f);
            }
            var linhaY = y + (altura - alturaTexto) / 2f;
            for (var i = 0; i < trechos.Count; i++)
            {
                DesenharTrecho(canvas, trechos[i], xTexto, larguraTexto, linhaY);
                linhaY += alturas[i];
            }
        }));
    }

    /// <summary>
    /// Três textos na mesma linha: esquerda, centro e direita. O do meio fica no meio do espaço entre os outros dois
    /// (centralizado quando os dois têm a mesma largura). Se os três não couberem (pedido de 4 ou 5 números com
    /// "(10/12)", papel de 58 mm), a letra diminui até caberem, sem um texto passar por cima do outro.
    /// </summary>
    public void Tres(string esquerda, string centro, string direita, SKTypeface fonte, float tamanho)
    {
        const float folga = 8;
        using (var medida = Pincel(fonte, tamanho))
        {
            var textos = medida.MeasureText(esquerda) + medida.MeasureText(centro) + medida.MeasureText(direita);
            var cabe = LarguraUtil - 2 * folga;
            if (textos > cabe) tamanho *= cabe / textos;
        }
        using var p0 = Pincel(fonte, tamanho);
        var altura = (int)Math.Ceiling(p0.FontMetrics.Descent - p0.FontMetrics.Ascent + 4);
        _blocos.Add((altura, (canvas, y) =>
        {
            using var p = Pincel(fonte, tamanho);
            var baseline = y + 2 - p.FontMetrics.Ascent;
            var larguraEsquerda = p.MeasureText(esquerda);
            var larguraCentro = p.MeasureText(centro);
            var larguraDireita = p.MeasureText(direita);
            var sobra = LarguraUtil - larguraEsquerda - larguraCentro - larguraDireita;
            canvas.DrawText(esquerda, Margem, baseline, p);
            canvas.DrawText(centro, Margem + larguraEsquerda + sobra / 2, baseline, p);
            canvas.DrawText(direita, Largura - Margem - larguraDireita, baseline, p);
        }));
    }

    /// <summary>Desenha a tira. Com <paramref name="moldura"/>, passa uma borda em volta, como nas fichas antigas.</summary>
    public SKBitmap Renderizar(bool moldura = false)
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
        if (moldura)
        {
            using var borda = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
            canvas.DrawRect(new SKRect(4.5f, 4.5f, Largura - 4.5f, altura - 4.5f), borda);
        }
        canvas.Flush();
        return bitmap;
    }

    private static float AlturaTrecho(Trecho t)
    {
        using var p = Pincel(t.Fonte, t.Tamanho);
        return (p.FontMetrics.Descent - p.FontMetrics.Ascent) * 1.1f + (t.Tarja ? 12 : 0);
    }

    private static void DesenharTrecho(SKCanvas canvas, Trecho t, float x, float largura, float topo)
    {
        if (string.IsNullOrEmpty(t.Texto) && string.IsNullOrEmpty(t.Direita)) return;
        using var p = Pincel(t.Fonte, t.Tamanho, t.Tarja ? SKColors.White : SKColors.Black);
        var larguraDireita = string.IsNullOrEmpty(t.Direita) ? 0 : p.MeasureText(t.Direita) + 12;
        const float folga = 16;
        var texto = Cortar(t.Texto, p, largura - larguraDireita - (t.Tarja ? 2 * folga : 0));
        var larguraTexto = p.MeasureText(texto) + (t.Tarja ? 2 * folga : 0);
        var xTexto = t.Alinhamento switch
        {
            Alinhamento.Centro => x + (largura - larguraTexto) / 2,
            Alinhamento.Direita => x + largura - larguraTexto,
            _ => x,
        };
        var baseline = topo + (t.Tarja ? 6 : 0) - p.FontMetrics.Ascent;
        if (t.Tarja)
        {
            using var fundo = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            var alturaTarja = p.FontMetrics.Descent - p.FontMetrics.Ascent + 12;
            canvas.DrawRoundRect(new SKRect(xTexto, topo, xTexto + larguraTexto, topo + alturaTarja), 6, 6, fundo);
            xTexto += folga;
        }
        canvas.DrawText(texto, xTexto, baseline, p);

        if (!string.IsNullOrEmpty(t.Direita))
        {
            using var pd = Pincel(t.Fonte, t.Tamanho);
            canvas.DrawText(t.Direita, x + largura - pd.MeasureText(t.Direita), baseline, pd);
        }
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
