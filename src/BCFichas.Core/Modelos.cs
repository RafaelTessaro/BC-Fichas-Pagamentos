namespace BCFichas.Core;

public enum FormaPagamento
{
    Dinheiro = 1,
    Debito = 2,
    Credito = 3,
    Pix = 4,
}

public enum StatusPedido
{
    AguardandoPagamento = 0,
    Pago = 1,
    Cancelado = 2,
}

public enum TipoMovimento
{
    /// <summary>Retirada de dinheiro do caixa.</summary>
    Sangria = 1,
    /// <summary>Entrada de dinheiro no caixa (troco, reforço).</summary>
    Suprimento = 2,
}

public static class Nomes
{
    public static string De(FormaPagamento forma) => forma switch
    {
        FormaPagamento.Dinheiro => "Dinheiro",
        FormaPagamento.Debito => "Débito",
        FormaPagamento.Credito => "Crédito",
        FormaPagamento.Pix => "PIX",
        _ => forma.ToString(),
    };

    public static string De(StatusPedido status) => status switch
    {
        StatusPedido.AguardandoPagamento => "Aguardando pagamento",
        StatusPedido.Pago => "Pago",
        StatusPedido.Cancelado => "Cancelado",
        _ => status.ToString(),
    };

    public static string De(TipoMovimento tipo) => tipo switch
    {
        TipoMovimento.Sangria => "Sangria (retirada)",
        TipoMovimento.Suprimento => "Suprimento (entrada)",
        _ => tipo.ToString(),
    };
}

public sealed class Aba
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public int Ordem { get; set; }
}

public sealed class Produto
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public string Detalhe { get; set; } = "";
    public long AbaId { get; set; }
    /// <summary>Posição do botão na grade da aba, começando em 1.</summary>
    public int Posicao { get; set; } = 1;
    /// <summary>Quantas fichas imprimir para cada unidade vendida.</summary>
    public int FichasPorUnidade { get; set; } = 1;
    public long CustoCentavos { get; set; }
    public long PrecoCentavos { get; set; }
    public bool ControlaEstoque { get; set; }
    public int Estoque { get; set; }
    public string Cor { get; set; } = "#0E9F6E";
    public string? Imagem { get; set; }
    /// <summary>Aparece como botão na tela de venda. Desligado: escondido (ex.: vale usado só dentro de combos).</summary>
    public bool Ativo { get; set; } = true;

    /// <summary>
    /// Combo: vende por um preço só e imprime as fichas destes produtos (ex.: COMBO HEINEKEN = 5 fichas de
    /// HEINEKEN de R$ 6,50; COMBO R$ 100 = vales de vários valores). No relatório aparece o combo.
    /// </summary>
    public List<ComponenteCombo> Componentes { get; set; } = new();

    public bool EhCombo => Componentes.Count > 0;

    /// <summary>Fichas que saem para cada unidade vendida.</summary>
    public int FichasPorVenda => EhCombo ? Componentes.Sum(c => c.Quantidade) : Math.Max(1, FichasPorUnidade);

    /// <summary>Soma do valor das fichas do combo (pode ser maior que o preço: o desconto do combo).</summary>
    public long ValorDasFichas => Componentes.Sum(c => c.TotalCentavos);

    public bool Esgotado => (ControlaEstoque && Estoque <= 0) ||
                            Componentes.Any(c => c.ControlaEstoque && c.Estoque < c.Quantidade);
}

/// <summary>Uma linha do combo: qual produto sai na ficha, quantas fichas e o valor impresso em cada uma.</summary>
public sealed class ComponenteCombo
{
    public long Id { get; set; }
    public long ProdutoId { get; set; }
    public string Nome { get; set; } = "";
    public string Detalhe { get; set; } = "";
    public int Quantidade { get; set; } = 1;
    /// <summary>Valor que sai em cada ficha (normalmente o preço do produto).</summary>
    public long ValorCentavos { get; set; }
    public bool ControlaEstoque { get; set; }
    public int Estoque { get; set; }

    public long TotalCentavos => ValorCentavos * Quantidade;
}

public sealed class SessaoCaixa
{
    public long Id { get; set; }
    public int Caixa { get; set; }
    public string Operador { get; set; } = "";
    public DateTime AbertaEm { get; set; }
    public long ValorAberturaCentavos { get; set; }
    public DateTime? FechadaEm { get; set; }
    public long? ValorContadoCentavos { get; set; }
    /// <summary>Caixa do modo teste (vendas de quem está programando a máquina): não entra nos relatórios.</summary>
    public bool Teste { get; set; }

    public bool Aberta => FechadaEm is null;
}

public sealed class Pedido
{
    public long Id { get; set; }
    public long Numero { get; set; }
    public long SessaoId { get; set; }
    public int Caixa { get; set; }
    public DateTime CriadoEm { get; set; }
    public long TotalCentavos { get; set; }
    public FormaPagamento Forma { get; set; }
    public long RecebidoCentavos { get; set; }
    public long TrocoCentavos { get; set; }
    public StatusPedido Status { get; set; }
    public string? Autorizacao { get; set; }
    public int Impressoes { get; set; }
    public bool Teste { get; set; }
    public List<ItemPedido> Itens { get; set; } = new();

    public int QuantidadeItens => Itens.Sum(i => i.Quantidade);
    public int QuantidadeFichas => Itens.Sum(i => i.Quantidade * i.FichasPorUnidade);
}

public sealed class ItemPedido
{
    public long Id { get; set; }
    public long PedidoId { get; set; }
    public long? ProdutoId { get; set; }
    public string Nome { get; set; } = "";
    public string Detalhe { get; set; } = "";
    public long PrecoCentavos { get; set; }
    public int Quantidade { get; set; }
    public int FichasPorUnidade { get; set; } = 1;
    /// <summary>Unidades que o cliente devolveu (ficha não usada). Nos combos a devolução é por ficha, em <see cref="Componentes"/>.</summary>
    public int Devolvidas { get; set; }
    /// <summary>Fichas de um combo, como estavam na hora da venda (para cada unidade vendida).</summary>
    public List<ComponenteItem> Componentes { get; set; } = new();

    public bool EhCombo => Componentes.Count > 0;
    public long TotalCentavos => PrecoCentavos * Quantidade;
    public int PodeDevolver => Quantidade - Devolvidas;
}

/// <summary>Fichas de um combo vendido (cópia do combo na hora da venda).</summary>
public sealed class ComponenteItem
{
    public long Id { get; set; }
    public long? ProdutoId { get; set; }
    public string Nome { get; set; } = "";
    public string Detalhe { get; set; } = "";
    public long ValorCentavos { get; set; }
    /// <summary>Fichas deste produto em cada unidade do combo.</summary>
    public int Quantidade { get; set; }
    /// <summary>Fichas deste produto que o cliente devolveu (somando todas as unidades do combo).</summary>
    public int Devolvidas { get; set; }
}

/// <summary>
/// Fichas que o cliente devolveu sem usar. A venda continua valendo; só a parte devolvida sai do total.
/// Em dinheiro, o valor sai da gaveta; no cartão e no PIX, o estorno é feito na maquininha.
/// </summary>
public sealed class Devolucao
{
    public long Id { get; set; }
    /// <summary>Caixa em que a devolução foi feita (pode ser outro, não o da venda).</summary>
    public long SessaoId { get; set; }
    public long PedidoId { get; set; }
    public long NumeroPedido { get; set; }
    public int Caixa { get; set; }
    public FormaPagamento Forma { get; set; }
    public long ValorCentavos { get; set; }
    public string Motivo { get; set; } = "";
    public DateTime CriadoEm { get; set; }
    public List<ItemDevolvido> Itens { get; set; } = new();

    public bool EmDinheiro => Forma == FormaPagamento.Dinheiro;
    public int QuantidadeItens => Itens.Sum(i => i.Quantidade);
}

public sealed class ItemDevolvido
{
    public long ItemId { get; set; }
    /// <summary>Ficha de um combo (nulo quando é o produto vendido sozinho).</summary>
    public long? ComponenteId { get; set; }
    public string Nome { get; set; } = "";
    /// <summary>Valor de cada unidade (aproximado nos combos, que devolvem a parte do preço do combo).</summary>
    public long PrecoCentavos { get; set; }
    public int Quantidade { get; set; }
    public long TotalCentavos { get; set; }
}

public sealed class Movimento
{
    public long Id { get; set; }
    public long SessaoId { get; set; }
    public int Caixa { get; set; }
    public TipoMovimento Tipo { get; set; }
    public long ValorCentavos { get; set; }
    public string Motivo { get; set; } = "";
    public string Usuario { get; set; } = "";
    public DateTime CriadoEm { get; set; }
}

public sealed record ProdutoVendido(string Nome, int Quantidade, long TotalCentavos);

public sealed class ResumoCaixa
{
    public required SessaoCaixa Sessao { get; init; }
    public Dictionary<FormaPagamento, long> PorForma { get; init; } = new();
    public Dictionary<FormaPagamento, int> PedidosPorForma { get; init; } = new();
    public int QuantidadePedidos { get; init; }
    public int QuantidadeFichas { get; init; }
    public long Sangrias { get; init; }
    public long Suprimentos { get; init; }
    public List<ProdutoVendido> Produtos { get; init; } = new();

    /// <summary>Devoluções feitas neste caixa, por forma de pagamento da venda original.</summary>
    public Dictionary<FormaPagamento, long> DevolucoesPorForma { get; init; } = new();
    public int QuantidadeDevolucoes { get; init; }
    public List<ProdutoVendido> ProdutosDevolvidos { get; init; } = new();

    public long TotalVendas => PorForma.Values.Sum();
    public long Total(FormaPagamento forma) => PorForma.GetValueOrDefault(forma);
    public long TotalDevolvido => DevolucoesPorForma.Values.Sum();
    public long Devolvido(FormaPagamento forma) => DevolucoesPorForma.GetValueOrDefault(forma);
    public bool TemDevolucoes => QuantidadeDevolucoes > 0;

    /// <summary>O que ficou de venda depois de tirar as fichas devolvidas.</summary>
    public long VendaLiquida => TotalVendas - TotalDevolvido;
    public long Liquido(FormaPagamento forma) => Total(forma) - Devolvido(forma);

    /// <summary>
    /// Dinheiro que deveria estar na gaveta: abertura + vendas em dinheiro + suprimentos - sangrias
    /// - fichas devolvidas em dinheiro.
    /// </summary>
    public long DinheiroEsperado =>
        Sessao.ValorAberturaCentavos + Total(FormaPagamento.Dinheiro) + Suprimentos - Sangrias
        - Devolvido(FormaPagamento.Dinheiro);

    public long TicketMedio => QuantidadePedidos == 0 ? 0 : TotalVendas / QuantidadePedidos;
}

/// <summary>Uma ficha física a ser impressa.</summary>
public sealed class Ficha
{
    public string NomeEvento { get; init; } = "";
    public string Produto { get; init; } = "";
    public string Detalhe { get; init; } = "";
    public long PrecoCentavos { get; init; }
    public long NumeroPedido { get; init; }
    public int Caixa { get; init; }
    public DateTime Data { get; init; }
    public int Sequencia { get; init; }
    public int TotalFichas { get; init; }
    public string Rodape { get; init; } = "";
    public bool Reimpressao { get; init; }
    public FormaPagamento Forma { get; init; }
    /// <summary>Ficha do modo teste: sai marcada "TESTE - SEM VALOR".</summary>
    public bool Teste { get; init; }

    /// <summary>Código único da ficha (usado no código de barras).</summary>
    public string Codigo => $"{Caixa:00}{NumeroPedido:000000}{Sequencia:000}";
}
