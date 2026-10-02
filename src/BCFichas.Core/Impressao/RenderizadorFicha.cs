using System.Globalization;
using SkiaSharp;

namespace BCFichas.Core.Impressao;

/// <summary>Desenha a ficha como imagem, do jeito que vai sair na impressora térmica.</summary>
public static class RenderizadorFicha
{
    /// <summary>Modelos na ordem em que aparecem nas configurações.</summary>
    public static readonly (ModeloFicha Modelo, string Nome)[] Modelos =
    [
        (ModeloFicha.Classico1, "Modelo 1 – valor na tarja preta"),
        (ModeloFicha.Classico2, "Modelo 2 – logo no topo"),
        (ModeloFicha.Classico3, "Modelo 3 – valor grande no topo"),
        (ModeloFicha.Classico4, "Modelo 4 – logo e valor embaixo"),
        (ModeloFicha.Completa, "Modelo 5 – completa (logo grande)"),
        (ModeloFicha.Compacta, "Modelo 6 – compacta (gasta menos papel)"),
        (ModeloFicha.Destaque, "Modelo 7 – faixa preta e valor em destaque"),
    ];

    public static SKBitmap Renderizar(Ficha ficha, Configuracao config, LogoFicha? logo)
    {
        var largura = config.LarguraPontos;
        var s = largura / 576f;
        // Com moldura, faixas de lado a lado param na borda (não passam para fora dela)
        var layout = new Layout(largura, (int)((config.Moldura ? 22 : 16) * s), config.Moldura ? Layout.LarguraBorda : 0);
        var d = new Dados(ficha, config, s);

        // O modelo 7 começa com a faixa preta: com moldura ela encosta na borda de cima
        if (config.Moldura) layout.Espaco(config.Modelo == ModeloFicha.Destaque ? Layout.LarguraBorda : (int)(8 * s));
        switch (config.Modelo)
        {
            case ModeloFicha.Classico1: Classico1(layout, d, logo); break;
            case ModeloFicha.Classico2: Classico2(layout, d, logo); break;
            case ModeloFicha.Classico3: Classico3(layout, d, logo); break;
            case ModeloFicha.Classico4: Classico4(layout, d, logo); break;
            case ModeloFicha.Compacta: Compacta(layout, d); break;
            case ModeloFicha.Destaque: Destaque(layout, d); break;
            default: Completa(layout, d, logo); break;
        }
        Final(layout, d);
        if (config.Moldura) layout.Espaco((int)(6 * s));

        return layout.Renderizar(config.Moldura);
    }

    /// <summary>Textos e fontes que todos os modelos usam.</summary>
    private sealed class Dados(Ficha f, Configuracao c, float s)
    {
        public Ficha Ficha { get; } = f;
        public Configuracao Config { get; } = c;
        public float S { get; } = s;
        public SKTypeface Produto { get; } = Fontes.Escolhida(c.Fonte);
        public SKTypeface Normal { get; } = Fontes.Normal;
        public SKTypeface Negrito { get; } = Fontes.Negrito;
        public bool MostrarValor => Config.MostrarValorNaFicha;
        public string Valor => Dinheiro.Formatar(Ficha.PrecoCentavos);
        public string DataHora => Ficha.Data.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        public string Forma => "FORMA: " + Nomes.De(Ficha.Forma).ToUpperInvariant();
        public string Caixa => $"CAIXA: {Ficha.Caixa:00}";

        /// <summary>"PED: 9", ou "PED: 9 (1/3)" quando o pedido tem várias fichas.</summary>
        public string Pedido => Ficha.TotalFichas > 1
            ? $"PED: {Ficha.NumeroPedido} ({Ficha.Sequencia}/{Ficha.TotalFichas})"
            : $"PED: {Ficha.NumeroPedido}";

        public int P(float pontos) => (int)(pontos * S);
        public float T(float tamanho) => tamanho * S;
    }

    // Modelo 1: evento; PRODUTO; [R$ tarja] + data/pedido + forma, com logo à direita; caixa à direita.
    private static void Classico1(Layout l, Dados d, LogoFicha? logo)
    {
        l.Texto(d.Ficha.NomeEvento, d.Negrito, d.T(24), maxLinhas: 1);
        Produto(l, d, 96);
        l.Espaco(d.P(4));
        var linhas = new List<Trecho>();
        if (d.MostrarValor) linhas.Add(new Trecho(d.Valor, d.Negrito, d.T(30), Tarja: true));
        linhas.Add(new Trecho($"{d.DataHora}   {d.Pedido}", d.Normal, d.T(21)));
        linhas.Add(new Trecho(d.Forma, d.Normal, d.T(21)));
        l.Coluna(linhas, logo?.Obter(d.P(150), d.P(96)), imagemADireita: true);
        l.Texto(d.Caixa, d.Normal, d.T(21), Alinhamento.Direita, 1);
    }

    // Modelo 2: logo à esquerda do evento e da data; PRODUTO; valor; forma e caixa.
    private static void Classico2(Layout l, Dados d, LogoFicha? logo)
    {
        l.Coluna(
        [
            new Trecho(d.Ficha.NomeEvento, d.Negrito, d.T(24), Alinhamento.Centro),
            new Trecho($"{d.DataHora}   {d.Pedido}", d.Normal, d.T(21), Alinhamento.Centro),
        ], logo?.Obter(d.P(130), d.P(84)), imagemADireita: false);
        l.Espaco(d.P(4));
        Produto(l, d, 96);
        l.Espaco(d.P(4));
        var linhas = new List<Trecho>();
        if (d.MostrarValor) linhas.Add(new Trecho(d.Valor, d.Normal, d.T(26)));
        linhas.Add(new Trecho(d.Forma, d.Normal, d.T(21), Direita: d.Caixa));
        l.Coluna(linhas);
    }

    // Modelo 3: valor grande, data/pedido e forma com logo à direita; PRODUTO; evento; caixa.
    private static void Classico3(Layout l, Dados d, LogoFicha? logo)
    {
        var linhas = new List<Trecho>();
        if (d.MostrarValor) linhas.Add(new Trecho(d.Valor, d.Normal, d.T(46)));
        linhas.Add(new Trecho($"{d.DataHora}   {d.Pedido}", d.Normal, d.T(21)));
        linhas.Add(new Trecho(d.Forma, d.Normal, d.T(21)));
        l.Coluna(linhas, logo?.Obter(d.P(150), d.P(96)), imagemADireita: true);
        Produto(l, d, 96);
        l.Texto(d.Ficha.NomeEvento, d.Negrito, d.T(24), maxLinhas: 1);
        l.Texto(d.Caixa, d.Normal, d.T(21), Alinhamento.Direita, 1);
    }

    // Modelo 4: evento; pedido, data e caixa; PRODUTO; logo à esquerda do valor grande e da forma.
    private static void Classico4(Layout l, Dados d, LogoFicha? logo)
    {
        l.Texto(d.Ficha.NomeEvento, d.Negrito, d.T(24), maxLinhas: 1);
        l.Tres(d.Pedido, d.DataHora, d.Caixa, d.Normal, d.T(20));
        Produto(l, d, 96);
        var linhas = new List<Trecho>();
        if (d.MostrarValor) linhas.Add(new Trecho(d.Valor, d.Normal, d.T(48), Alinhamento.Direita));
        linhas.Add(new Trecho(d.Forma, d.Normal, d.T(21), Alinhamento.Centro));
        l.Coluna(linhas, logo?.Obter(d.P(150), d.P(96)), imagemADireita: false);
    }

    private static void Completa(Layout l, Dados d, LogoFicha? logo)
    {
        l.Espaco(d.P(4));
        if (logo is not null)
        {
            l.Imagem(logo.Obter(d.P(300), d.P(150)));
            l.Espaco(d.P(6));
        }
        l.Texto(d.Ficha.NomeEvento, d.Negrito, d.T(34), maxLinhas: 2);
        l.Espaco(d.P(4));
        l.Separador();
        l.Espaco(d.P(6));
        Produto(l, d, 110);
        if (d.MostrarValor) l.Texto(d.Valor, d.Negrito, d.T(46), maxLinhas: 1);
        l.Espaco(d.P(6));
        l.Separador();
        l.Par(d.Pedido, d.DataHora, d.Normal, d.T(22));
        l.Par(d.Forma, d.Caixa, d.Normal, d.T(22));
    }

    private static void Compacta(Layout l, Dados d)
    {
        l.Texto(d.Ficha.NomeEvento, d.Negrito, d.T(22), maxLinhas: 1);
        Produto(l, d, 96);
        var partes = new List<string>();
        if (d.MostrarValor) partes.Add(d.Valor);
        partes.Add(d.Pedido);
        partes.Add(d.Ficha.Data.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture));
        partes.Add(d.Caixa);
        l.Texto(string.Join("  •  ", partes), d.Normal, d.T(20), maxLinhas: 2);
    }

    private static void Destaque(Layout l, Dados d)
    {
        l.Faixa(d.Ficha.NomeEvento, d.Negrito, d.T(30), d.P(8));
        l.Espaco(d.P(8));
        Produto(l, d, 116);
        if (d.MostrarValor)
        {
            l.Espaco(d.P(6));
            l.Selo(d.Valor, d.Negrito, d.T(44));
        }
        l.Espaco(d.P(8));
        l.Par(d.Pedido, d.DataHora, d.Normal, d.T(21));
        l.Par(d.Forma, d.Caixa, d.Normal, d.T(21));
    }

    /// <summary>Nome do produto o maior possível (até 2 linhas) e o detalhe embaixo.</summary>
    private static void Produto(Layout l, Dados d, float tamanhoMaximo)
    {
        l.TextoAjustado(d.Ficha.Produto, d.Produto, d.T(tamanhoMaximo), d.T(40), 2);
        l.Texto(d.Ficha.Detalhe, d.Normal, d.T(24), maxLinhas: 1);
    }

    /// <summary>Marca de reimpressão, código de barras, rodapé e a mensagem fixa (iguais em todos os modelos).</summary>
    private static void Final(Layout l, Dados d)
    {
        if (d.Ficha.Reimpressao)
        {
            l.Espaco(d.P(6));
            l.Texto("REIMPRESSÃO", d.Negrito, d.T(24), maxLinhas: 1, invertido: true);
        }
        if (d.Config.CodigoDeBarras)
        {
            l.Espaco(d.P(6));
            l.CodigoDeBarras(d.Ficha.Codigo, d.P(56), d.Normal, d.T(16));
        }
        var rodape = Configuracao.RodapeSemMensagemFixa(d.Ficha.Rodape);
        if (rodape.Length > 0)
        {
            l.Espaco(d.P(6));
            l.Texto(rodape, d.Negrito, d.T(21), maxLinhas: 2);
        }
        // O telefone da BC Fichas sai em toda ficha, sempre por último
        l.Espaco(d.P(rodape.Length > 0 ? 2 : 6));
        l.Texto(Configuracao.MensagemFixa, d.Negrito, d.T(19), maxLinhas: 1);
        l.Espaco(d.P(4));
    }

    /// <summary>Ficha de mentira para a prévia da tela de configuração.</summary>
    public static Ficha Exemplo(Configuracao config) => new()
    {
        NomeEvento = config.NomeEvento,
        Produto = "PASTEL",
        PrecoCentavos = 1000,
        NumeroPedido = 9,
        Caixa = config.NumeroCaixa,
        Data = DateTime.Now,
        Sequencia = 1,
        TotalFichas = 1,
        Rodape = config.Rodape,
        Forma = FormaPagamento.Dinheiro,
    };
}
