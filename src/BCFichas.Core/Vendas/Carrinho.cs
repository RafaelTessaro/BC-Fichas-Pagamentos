namespace BCFichas.Core.Vendas;

public sealed class LinhaCarrinho
{
    public required Produto Produto { get; init; }
    public int Quantidade { get; set; }
    public long TotalCentavos => Produto.PrecoCentavos * Quantidade;
}

/// <summary>Itens do pedido sendo montado no balcão.</summary>
public sealed class Carrinho
{
    private readonly List<LinhaCarrinho> _linhas = new();

    public IReadOnlyList<LinhaCarrinho> Linhas => _linhas;
    public long TotalCentavos => _linhas.Sum(l => l.TotalCentavos);
    public int QuantidadeItens => _linhas.Sum(l => l.Quantidade);
    public bool Vazio => _linhas.Count == 0;

    /// <summary>Adiciona uma unidade. Retorna falso se não houver estoque.</summary>
    public bool Adicionar(Produto produto, int quantidade = 1)
    {
        var linha = _linhas.FirstOrDefault(l => l.Produto.Id == produto.Id);
        if (!CabeNoEstoque(produto, quantidade)) return false;

        if (linha is null)
            _linhas.Add(new LinhaCarrinho { Produto = produto, Quantidade = quantidade });
        else
            linha.Quantidade += quantidade;
        return true;
    }

    /// <summary>
    /// Confere o estoque somando o pedido inteiro: combos gastam o estoque dos produtos das fichas (um combo de
    /// 5 HEINEKEN mais 2 HEINEKEN avulsas precisam de 7).
    /// </summary>
    private bool CabeNoEstoque(Produto novo, int quantidade)
    {
        var precisa = new Dictionary<long, (int Quantidade, int Estoque)>();
        void Somar(long id, bool controla, int estoque, int q)
        {
            if (!controla) return;
            var atual = precisa.GetValueOrDefault(id);
            precisa[id] = (atual.Quantidade + q, estoque);
        }
        void Linha(Produto p, int q)
        {
            Somar(p.Id, p.ControlaEstoque, p.Estoque, q);
            foreach (var c in p.Componentes.Where(c => c.ProdutoId is not null))
                Somar(c.ProdutoId!.Value, c.ControlaEstoque, c.Estoque, q * c.Quantidade);
        }
        foreach (var l in _linhas) Linha(l.Produto, l.Quantidade);
        Linha(novo, quantidade);
        return precisa.Values.All(p => p.Quantidade <= p.Estoque);
    }

    public void Remover(long produtoId, int quantidade = 1)
    {
        var linha = _linhas.FirstOrDefault(l => l.Produto.Id == produtoId);
        if (linha is null) return;
        linha.Quantidade -= quantidade;
        if (linha.Quantidade <= 0) _linhas.Remove(linha);
    }

    public void RemoverTudo(long produtoId) => _linhas.RemoveAll(l => l.Produto.Id == produtoId);

    public void Limpar() => _linhas.Clear();
}
