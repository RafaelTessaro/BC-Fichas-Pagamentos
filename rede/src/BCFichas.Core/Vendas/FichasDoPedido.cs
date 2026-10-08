namespace BCFichas.Core.Vendas;

/// <summary>
/// Uma linha do pedido do jeito que as fichas saem: o produto vendido sozinho (devolve por unidade) ou, no
/// combo, cada produto das fichas (devolve ficha por ficha).
/// </summary>
public sealed class LinhaDeFichas
{
    public required ItemPedido Item { get; init; }
    public ComponenteItem? Componente { get; init; }

    /// <summary>Parte do preço do combo que cabe a esta linha (somando todas as unidades vendidas).</summary>
    internal long Parte { get; init; }

    public bool DeCombo => Componente is not null;
    public string Nome => Componente?.Nome ?? Item.Nome;
    /// <summary>
    /// Linha de baixo da ficha: no combo, só o detalhe da ficha (ex.: VAL. 05/10/26). Sem detalhe, a ficha do combo
    /// sai igual à do produto vendido sozinho (sem o nome do combo embaixo).
    /// </summary>
    public string Detalhe => Componente?.Detalhe ?? Item.Detalhe;
    /// <summary>Valor impresso na ficha.</summary>
    public long ValorNaFicha => Componente?.ValorCentavos ?? Item.PrecoCentavos;

    /// <summary>Unidades vendidas: produtos inteiros ou, no combo, fichas deste produto.</summary>
    public int Total => Componente is null ? Item.Quantidade : Item.Quantidade * Componente.Quantidade;
    public int Devolvidas => Componente?.Devolvidas ?? Item.Devolvidas;
    public int PodeDevolver => Total - Devolvidas;
    /// <summary>Fichas impressas para cada unidade da linha.</summary>
    public int FichasPorUnidade => Componente is null ? Math.Max(1, Item.FichasPorUnidade) : 1;
    public int Fichas => Total * FichasPorUnidade;

    /// <summary>
    /// Quanto volta para o cliente ao devolver mais <paramref name="quantidade"/>. No combo é a parte do preço do
    /// combo (ex.: combo de R$ 30 com 5 fichas de R$ 6,50: cada ficha devolvida vale R$ 6,00); devolvendo todas,
    /// a soma dá o preço do combo certinho.
    /// </summary>
    public long Valor(int quantidade, int? jaDevolvidas = null)
    {
        var ja = jaDevolvidas ?? Devolvidas;
        if (Componente is null) return Item.PrecoCentavos * quantidade;
        if (Total == 0) return 0;
        return Parte * (ja + quantidade) / Total - Parte * ja / Total;
    }

    /// <summary>Valor de uma unidade (no combo, a média: a última ficha pode ter um centavo a mais).</summary>
    public long ValorUnitario => Componente is null ? Item.PrecoCentavos : Total == 0 ? 0 : Parte / Total;
}

public static class FichasDoPedido
{
    /// <summary>Linhas na ordem em que as fichas são impressas (a mesma da numeração "3/6").</summary>
    public static List<LinhaDeFichas> Linhas(Pedido pedido)
    {
        var linhas = new List<LinhaDeFichas>();
        foreach (var item in pedido.Itens)
        {
            if (!item.EhCombo)
            {
                linhas.Add(new LinhaDeFichas { Item = item });
                continue;
            }
            // O preço do combo é repartido entre as fichas pelo valor de cada uma.
            var pesos = item.Componentes.Select(c => c.ValorCentavos * c.Quantidade).ToArray();
            if (pesos.All(p => p == 0)) pesos = item.Componentes.Select(c => (long)c.Quantidade).ToArray();
            var partes = Repartir(item.PrecoCentavos * item.Quantidade, pesos);
            for (var i = 0; i < item.Componentes.Count; i++)
                linhas.Add(new LinhaDeFichas { Item = item, Componente = item.Componentes[i], Parte = partes[i] });
        }
        return linhas;
    }

    /// <summary>Linha que imprimiu a ficha de número <paramref name="sequencia"/> ("PED: 13 (3/6)" é a 3).</summary>
    public static LinhaDeFichas? LinhaDaFicha(Pedido pedido, int sequencia)
    {
        var inicio = 0;
        foreach (var linha in Linhas(pedido))
        {
            if (sequencia > inicio && sequencia <= inicio + linha.Fichas) return linha;
            inicio += linha.Fichas;
        }
        return null;
    }

    /// <summary>Divide um total em partes proporcionais aos pesos, sem perder centavo (maiores restos).</summary>
    internal static long[] Repartir(long total, long[] pesos)
    {
        var soma = pesos.Sum();
        var partes = new long[pesos.Length];
        if (soma <= 0 || pesos.Length == 0) return partes;
        var restos = new (long Resto, int Indice)[pesos.Length];
        long distribuido = 0;
        for (var i = 0; i < pesos.Length; i++)
        {
            partes[i] = Math.DivRem(total * pesos[i], soma, out var resto);
            restos[i] = (resto, i);
            distribuido += partes[i];
        }
        foreach (var (_, indice) in restos.OrderByDescending(r => r.Resto).ThenBy(r => r.Indice).Take((int)(total - distribuido)))
            partes[indice]++;
        return partes;
    }
}
