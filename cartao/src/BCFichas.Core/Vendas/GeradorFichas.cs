namespace BCFichas.Core.Vendas;

public static class GeradorFichas
{
    /// <summary>
    /// Uma ficha por unidade vendida (vezes as fichas por unidade do produto). No combo saem as fichas dos
    /// produtos do combo, com o valor de cada uma, iguais às do produto vendido sozinho.
    /// Com <paramref name="somenteItemId"/>, reimprime só as fichas daquele item.
    /// Na reimpressão, as fichas que o cliente devolveu ficam de fora (a numeração "1/6" não muda).
    /// </summary>
    public static List<Ficha> Gerar(Pedido pedido, Configuracao config, bool reimpressao = false,
        long? somenteItemId = null)
    {
        var total = pedido.QuantidadeFichas;
        var fichas = new List<Ficha>(total);
        var sequencia = 0;

        foreach (var linha in FichasDoPedido.Linhas(pedido))
        {
            var validas = linha.PodeDevolver;
            for (var unidade = 0; unidade < linha.Total; unidade++)
            {
                for (var f = 0; f < linha.FichasPorUnidade; f++)
                {
                    sequencia++;
                    if (somenteItemId is not null && linha.Item.Id != somenteItemId) continue;
                    if (unidade >= validas) continue;

                    fichas.Add(new Ficha
                    {
                        NomeEvento = config.NomeEvento,
                        Produto = linha.Nome,
                        Detalhe = linha.Detalhe,
                        PrecoCentavos = linha.ValorNaFicha,
                        NumeroPedido = pedido.Numero,
                        Caixa = pedido.Caixa,
                        Data = pedido.CriadoEm,
                        Sequencia = sequencia,
                        TotalFichas = total,
                        Rodape = config.Rodape,
                        Reimpressao = reimpressao,
                        Forma = pedido.Forma,
                        Teste = pedido.Teste,
                    });
                }
            }
        }

        return fichas;
    }
}
