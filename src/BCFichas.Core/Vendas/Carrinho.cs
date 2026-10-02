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
        var atual = linha?.Quantidade ?? 0;
        if (produto.ControlaEstoque && atual + quantidade > produto.Estoque) return false;

        if (linha is null)
            _linhas.Add(new LinhaCarrinho { Produto = produto, Quantidade = quantidade });
        else
            linha.Quantidade += quantidade;
        return true;
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
