using System.Globalization;

namespace BCFichas.Core.Impressao;

public static class Relatorios
{
    private static string Data(DateTime d) => d.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Comprovante de abertura: quanto de troco o caixa recebeu e quando.</summary>
    public static Documento Abertura(SessaoCaixa s, Configuracao config) =>
        new Documento()
            .Titulo(config.NomeEvento)
            .Faixa("ABERTURA DE CAIXA")
            .Espaco(6)
            .Par($"CAIXA {s.Caixa:00}", $"SESSÃO {s.Id}")
            .Par("DATA", s.AbertaEm.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))
            .Par("HORA", s.AbertaEm.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            .ParSeHouver("OPERADOR", s.Operador)
            .Separador()
            .Par("TROCO INICIAL", Dinheiro.Formatar(s.ValorAberturaCentavos), negrito: true, tamanho: 32)
            .Linha("Dinheiro colocado na gaveta para começar o caixa.", tamanho: 20)
            .Espaco(36)
            .Linha("______________________________", Alinhamento.Centro)
            .Linha("ASSINATURA DO RESPONSÁVEL", Alinhamento.Centro, tamanho: 20);

    /// <summary>Comprovante da devolução de fichas (fica com o caixa, junto com as fichas devolvidas).</summary>
    public static Documento Devolucao(Devolucao d, Pedido pedido, Configuracao config, long dinheiroNoCaixa)
    {
        var doc = new Documento()
            .Titulo(config.NomeEvento)
            .Faixa("DEVOLUÇÃO DE FICHAS")
            .Espaco(6)
            .Par($"CAIXA {d.Caixa:00}", Data(d.CriadoEm))
            .Par("PEDIDO", $"{pedido.Numero} • {Data(pedido.CriadoEm)}")
            .Par("PAGO EM", Nomes.De(pedido.Forma).ToUpperInvariant())
            .Separador()
            .Linha("FICHAS DEVOLVIDAS", negrito: true);
        foreach (var item in d.Itens)
            doc.Par($"{item.Quantidade} x {item.Nome}", Dinheiro.Formatar(item.TotalCentavos), tamanho: 22);
        doc.Separador(tracejado: false)
            .Par("VALOR DEVOLVIDO", Dinheiro.Formatar(d.ValorCentavos), negrito: true, tamanho: 30)
            .Linha(d.EmDinheiro
                    ? "DEVOLVIDO EM DINHEIRO AO CLIENTE"
                    : $"ESTORNO FEITO NA MAQUININHA ({Nomes.De(d.Forma).ToUpperInvariant()})",
                Alinhamento.Centro, negrito: true, tamanho: 22);
        if (!string.IsNullOrWhiteSpace(d.Motivo)) doc.Linha("MOTIVO: " + d.Motivo, tamanho: 22);
        if (d.EmDinheiro) doc.Par("DINHEIRO NO CAIXA", Dinheiro.Formatar(dinheiroNoCaixa));
        return doc.Espaco(30)
            .Linha("______________________________", Alinhamento.Centro)
            .Linha("ASSINATURA", Alinhamento.Centro, tamanho: 20);
    }

    /// <summary>Fechamento (ou parcial) do caixa com totais por forma de pagamento.</summary>
    public static Documento Fechamento(ResumoCaixa resumo, Configuracao config, bool parcial = false)
    {
        var s = resumo.Sessao;
        var doc = new Documento()
            .Titulo(config.NomeEvento)
            .Faixa(parcial ? "PARCIAL DO CAIXA" : "FECHAMENTO DE CAIXA")
            .Espaco(6)
            .Par($"CAIXA {s.Caixa:00}", $"SESSÃO {s.Id}")
            .ParSeHouver("OPERADOR", s.Operador)
            .Par("ABERTURA", Data(s.AbertaEm))
            .Par(parcial ? "EMITIDO" : "FECHAMENTO", Data(s.FechadaEm ?? DateTime.Now))
            .Separador()
            .Linha("VENDAS", negrito: true);

        foreach (var forma in Enum.GetValues<FormaPagamento>())
        {
            var qtd = resumo.PedidosPorForma.GetValueOrDefault(forma);
            doc.Par($"{Nomes.De(forma).ToUpperInvariant()} ({qtd})", Dinheiro.Formatar(resumo.Total(forma)));
        }

        doc.Separador(tracejado: false)
            .Par("TOTAL VENDIDO", Dinheiro.Formatar(resumo.TotalVendas), negrito: true, tamanho: 28)
            .Par("PEDIDOS", resumo.QuantidadePedidos.ToString(CultureInfo.InvariantCulture))
            .Par("FICHAS", resumo.QuantidadeFichas.ToString(CultureInfo.InvariantCulture))
            .Par("TICKET MÉDIO", Dinheiro.Formatar(resumo.TicketMedio));

        if (resumo.TemDevolucoes)
        {
            doc.Separador()
                .Linha($"FICHAS DEVOLVIDAS ({resumo.QuantidadeDevolucoes})", negrito: true);
            foreach (var forma in Enum.GetValues<FormaPagamento>().Where(f => resumo.Devolvido(f) > 0))
                doc.Par(forma == FormaPagamento.Dinheiro ? "DINHEIRO (DA GAVETA)" : $"{Nomes.De(forma).ToUpperInvariant()} (MAQUININHA)",
                    "- " + Dinheiro.Formatar(resumo.Devolvido(forma)));
            doc.Par("TOTAL DEVOLVIDO", "- " + Dinheiro.Formatar(resumo.TotalDevolvido), negrito: true)
                .Separador(tracejado: false)
                .Par("VENDA LÍQUIDA", Dinheiro.Formatar(resumo.VendaLiquida), negrito: true, tamanho: 28);
        }

        doc.Separador()
            .Linha("DINHEIRO NA GAVETA", negrito: true)
            .Par("ABERTURA (TROCO)", Dinheiro.Formatar(s.ValorAberturaCentavos))
            .Par("+ VENDAS EM DINHEIRO", Dinheiro.Formatar(resumo.Total(FormaPagamento.Dinheiro)))
            .Par("+ SUPRIMENTOS", Dinheiro.Formatar(resumo.Suprimentos))
            .Par("- SANGRIAS", Dinheiro.Formatar(resumo.Sangrias));
        if (resumo.Devolvido(FormaPagamento.Dinheiro) > 0)
            doc.Par("- DEVOLUÇÕES EM DINHEIRO", Dinheiro.Formatar(resumo.Devolvido(FormaPagamento.Dinheiro)));
        doc.Par("= ESPERADO", Dinheiro.Formatar(resumo.DinheiroEsperado), negrito: true);

        if (s.ValorContadoCentavos is { } contado)
        {
            var diferenca = contado - resumo.DinheiroEsperado;
            doc.Par("CONTADO", Dinheiro.Formatar(contado))
                .Par(diferenca == 0 ? "DIFERENÇA" : diferenca > 0 ? "SOBRA" : "FALTA",
                    Dinheiro.Formatar(Math.Abs(diferenca)), negrito: true);
        }

        if (resumo.Produtos.Count > 0)
        {
            doc.Separador().Linha("PRODUTOS VENDIDOS", negrito: true);
            foreach (var p in resumo.Produtos)
                doc.Par($"{p.Quantidade} x {p.Nome}", Dinheiro.Formatar(p.TotalCentavos), tamanho: 22);
        }
        if (resumo.ProdutosDevolvidos.Count > 0)
        {
            doc.Separador().Linha("PRODUTOS DEVOLVIDOS", negrito: true);
            foreach (var p in resumo.ProdutosDevolvidos)
                doc.Par($"{p.Quantidade} x {p.Nome}", "- " + Dinheiro.Formatar(p.TotalCentavos), tamanho: 22);
        }

        if (!parcial)
        {
            doc.Espaco(30)
                .Linha("______________________________", Alinhamento.Centro)
                .Linha("ASSINATURA DO OPERADOR", Alinhamento.Centro, tamanho: 20);
        }
        return doc;
    }

    public static Documento Movimento(Movimento m, Configuracao config, long dinheiroNoCaixa) =>
        new Documento()
            .Titulo(config.NomeEvento)
            .Faixa(m.Tipo == TipoMovimento.Sangria ? "SANGRIA (RETIRADA)" : "SUPRIMENTO (ENTRADA)")
            .Espaco(6)
            .Par($"CAIXA {m.Caixa:00}", Data(m.CriadoEm))
            .ParSeHouver("OPERADOR", m.Usuario)
            .Separador()
            .Par("VALOR", Dinheiro.Formatar(m.ValorCentavos), negrito: true, tamanho: 30)
            .Linha(string.IsNullOrWhiteSpace(m.Motivo) ? "" : "MOTIVO: " + m.Motivo)
            .Par("DINHEIRO NO CAIXA", Dinheiro.Formatar(dinheiroNoCaixa))
            .Espaco(30)
            .Linha("______________________________", Alinhamento.Centro)
            .Linha("ASSINATURA", Alinhamento.Centro, tamanho: 20);

    public static Documento Teste(Configuracao config, string destino) =>
        new Documento { Moldura = true }
            .Titulo("TESTE DE IMPRESSÃO")
            .Linha(config.NomeEvento, Alinhamento.Centro)
            .Separador()
            .Par("DESTINO", destino, tamanho: 20)
            .Par("PAPEL", $"{config.LarguraPapelMm} mm ({config.LarguraPontos} pontos)")
            .Par("POSIÇÃO", AjusteEmMm(config.AjusteHorizontal))
            .Par("DATA", Data(DateTime.Now))
            .Separador()
            .Linha("ÁÉÍÓÚ ÂÊÔ ÃÕ Ç  áéíóú âêô ãõ ç", Alinhamento.Centro)
            .Linha("Se você consegue ler esta linha, a impressora está funcionando!", Alinhamento.Centro)
            .Separador()
            .Linha("A borda deve ficar com a mesma folga dos dois lados do papel. Se ficar mais para um lado, " +
                   "ajuste a posição em Configurações > Impressora.", Alinhamento.Centro, tamanho: 20);

    /// <summary>"centralizada", "1,5 mm para a esquerda"...</summary>
    public static string AjusteEmMm(int pontos)
    {
        if (pontos == 0) return "CENTRALIZADA";
        var mm = (Math.Abs(pontos) / 8.0).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
        return $"{mm} MM PARA A {(pontos < 0 ? "ESQUERDA" : "DIREITA")}";
    }
}
