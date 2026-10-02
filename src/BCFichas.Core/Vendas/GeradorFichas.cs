namespace BCFichas.Core.Vendas;

public static class GeradorFichas
{
    /// <summary>
    /// Uma ficha por unidade vendida (vezes as fichas por unidade do produto).
    /// Com <paramref name="somenteItemId"/>, reimprime só as fichas daquele item.
    /// </summary>
    public static List<Ficha> Gerar(Pedido pedido, Configuracao config, bool reimpressao = false,
        long? somenteItemId = null)
    {
        var total = pedido.QuantidadeFichas;
        var fichas = new List<Ficha>(total);
        var sequencia = 0;

        foreach (var item in pedido.Itens)
        {
            var fichasDoItem = item.Quantidade * Math.Max(1, item.FichasPorUnidade);
            for (var i = 0; i < fichasDoItem; i++)
            {
                sequencia++;
                if (somenteItemId is not null && item.Id != somenteItemId) continue;

                fichas.Add(new Ficha
                {
                    NomeEvento = config.NomeEvento,
                    Produto = item.Nome,
                    Detalhe = item.Detalhe,
                    PrecoCentavos = item.PrecoCentavos,
                    NumeroPedido = pedido.Numero,
                    Caixa = pedido.Caixa,
                    Data = pedido.CriadoEm,
                    Sequencia = sequencia,
                    TotalFichas = total,
                    Rodape = config.Rodape,
                    Reimpressao = reimpressao,
                    Forma = pedido.Forma,
                });
            }
        }

        return fichas;
    }
}
