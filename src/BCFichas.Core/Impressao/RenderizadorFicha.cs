using SkiaSharp;

namespace BCFichas.Core.Impressao;

/// <summary>Desenha a ficha como imagem, do jeito que vai sair na impressora térmica.</summary>
public static class RenderizadorFicha
{
    public static SKBitmap Renderizar(Ficha ficha, Configuracao config, SKBitmap? logo)
    {
        var largura = config.LarguraPontos;
        var s = largura / 576f;
        var layout = new Layout(largura, (int)(16 * s));
        var fonteProduto = Fontes.Escolhida(config.Fonte);
        var normal = Fontes.Normal;
        var negrito = Fontes.Negrito;
        var valor = Dinheiro.Formatar(ficha.PrecoCentavos);
        var data = ficha.Data.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var fichaDe = $"FICHA {ficha.Sequencia}/{ficha.TotalFichas}";
        var pedido = $"PEDIDO {ficha.NumeroPedido:000000}";

        switch (config.Modelo)
        {
            case ModeloFicha.Compacta:
                layout.Espaco((int)(6 * s));
                layout.Texto(ficha.NomeEvento, negrito, 24 * s, maxLinhas: 1);
                layout.TextoAjustado(ficha.Produto, fonteProduto, 100 * s, 40 * s, 2);
                layout.Texto(ficha.Detalhe, normal, 26 * s, maxLinhas: 1);
                layout.Espaco((int)(4 * s));
                var partes = new List<string>();
                if (config.MostrarValorNaFicha) partes.Add(valor);
                partes.Add($"PED {ficha.NumeroPedido:000000}");
                partes.Add($"{ficha.Sequencia}/{ficha.TotalFichas}");
                partes.Add(ficha.Data.ToString("dd/MM HH:mm", System.Globalization.CultureInfo.InvariantCulture));
                layout.Texto(string.Join("  •  ", partes), normal, 21 * s, maxLinhas: 2);
                Rodape(layout, ficha, config, normal, negrito, s, alturaCodigo: 50, mostrarRodape: false);
                break;

            case ModeloFicha.Destaque:
                layout.Faixa(ficha.NomeEvento, negrito, 32 * s, (int)(10 * s));
                layout.Espaco((int)(10 * s));
                layout.TextoAjustado(ficha.Produto, fonteProduto, 120 * s, 44 * s, 2);
                layout.Texto(ficha.Detalhe, normal, 30 * s, maxLinhas: 2);
                if (config.MostrarValorNaFicha)
                {
                    layout.Espaco((int)(8 * s));
                    layout.Selo(valor, negrito, 48 * s);
                }
                layout.Espaco((int)(10 * s));
                layout.Par(pedido, fichaDe, normal, 22 * s);
                layout.Par($"CAIXA {ficha.Caixa:00}", data, normal, 22 * s);
                Rodape(layout, ficha, config, normal, negrito, s, alturaCodigo: 64, mostrarRodape: true);
                break;

            default:
                layout.Espaco((int)(10 * s));
                if (logo is not null)
                {
                    layout.Imagem(logo);
                    layout.Espaco((int)(6 * s));
                }
                layout.Texto(ficha.NomeEvento, negrito, 34 * s, maxLinhas: 2);
                layout.Espaco((int)(4 * s));
                layout.Separador();
                layout.Espaco((int)(6 * s));
                layout.TextoAjustado(ficha.Produto, fonteProduto, 110 * s, 44 * s, 2);
                layout.Texto(ficha.Detalhe, normal, 30 * s, maxLinhas: 2);
                if (config.MostrarValorNaFicha) layout.Texto(valor, negrito, 46 * s, maxLinhas: 1);
                layout.Espaco((int)(6 * s));
                layout.Separador();
                layout.Par(pedido, fichaDe, normal, 24 * s);
                layout.Par($"CAIXA {ficha.Caixa:00} • {Nomes.De(ficha.Forma).ToUpperInvariant()}", data, normal, 24 * s);
                Rodape(layout, ficha, config, normal, negrito, s, alturaCodigo: 70, mostrarRodape: true);
                break;
        }

        return layout.Renderizar();
    }

    private static void Rodape(Layout layout, Ficha ficha, Configuracao config, SKTypeface normal, SKTypeface negrito,
        float s, int alturaCodigo, bool mostrarRodape)
    {
        if (ficha.Reimpressao)
        {
            layout.Espaco((int)(6 * s));
            layout.Texto("REIMPRESSÃO", negrito, 26 * s, maxLinhas: 1, invertido: true);
        }

        if (config.CodigoDeBarras)
        {
            layout.Espaco((int)(8 * s));
            layout.CodigoDeBarras(ficha.Codigo, (int)(alturaCodigo * s), normal, 18 * s);
        }

        if (mostrarRodape && !string.IsNullOrWhiteSpace(ficha.Rodape))
        {
            layout.Espaco((int)(6 * s));
            layout.Texto(ficha.Rodape, normal, 24 * s, maxLinhas: 3);
        }

        layout.Espaco((int)(12 * s));
    }

    /// <summary>Ficha de mentira para a prévia da tela de configuração.</summary>
    public static Ficha Exemplo(Configuracao config) => new()
    {
        NomeEvento = config.NomeEvento,
        Produto = "PASTEL",
        Detalhe = "CARNE OU QUEIJO",
        PrecoCentavos = 1000,
        NumeroPedido = 123,
        Caixa = config.NumeroCaixa,
        Data = DateTime.Now,
        Sequencia = 1,
        TotalFichas = 3,
        Rodape = config.Rodape,
        Forma = FormaPagamento.Pix,
    };
}
