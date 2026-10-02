using System.Globalization;

namespace BCFichas.Core.Impressao;

public static class Relatorios
{
    private static string Data(DateTime d) => d.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Fechamento (ou parcial) do caixa com totais por forma de pagamento.</summary>
    public static Documento Fechamento(ResumoCaixa resumo, Configuracao config, bool parcial = false)
    {
        var s = resumo.Sessao;
        var doc = new Documento()
            .Titulo(config.NomeEvento)
            .Faixa(parcial ? "PARCIAL DO CAIXA" : "FECHAMENTO DE CAIXA")
            .Espaco(6)
            .Par($"CAIXA {s.Caixa:00}", $"SESSÃO {s.Id}")
            .Par("OPERADOR", s.Operador)
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
            .Par("TICKET MÉDIO", Dinheiro.Formatar(resumo.TicketMedio))
            .Separador()
            .Linha("DINHEIRO NA GAVETA", negrito: true)
            .Par("ABERTURA (TROCO)", Dinheiro.Formatar(s.ValorAberturaCentavos))
            .Par("+ VENDAS EM DINHEIRO", Dinheiro.Formatar(resumo.Total(FormaPagamento.Dinheiro)))
            .Par("+ SUPRIMENTOS", Dinheiro.Formatar(resumo.Suprimentos))
            .Par("- SANGRIAS", Dinheiro.Formatar(resumo.Sangrias))
            .Par("= ESPERADO", Dinheiro.Formatar(resumo.DinheiroEsperado), negrito: true);

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
            .Par("OPERADOR", m.Usuario)
            .Separador()
            .Par("VALOR", Dinheiro.Formatar(m.ValorCentavos), negrito: true, tamanho: 30)
            .Linha(string.IsNullOrWhiteSpace(m.Motivo) ? "" : "MOTIVO: " + m.Motivo)
            .Par("DINHEIRO NO CAIXA", Dinheiro.Formatar(dinheiroNoCaixa))
            .Espaco(30)
            .Linha("______________________________", Alinhamento.Centro)
            .Linha("ASSINATURA", Alinhamento.Centro, tamanho: 20);

    public static Documento Teste(Configuracao config, string destino) =>
        new Documento()
            .Titulo("TESTE DE IMPRESSÃO")
            .Linha(config.NomeEvento, Alinhamento.Centro)
            .Separador()
            .Par("DESTINO", destino, tamanho: 20)
            .Par("PAPEL", $"{config.LarguraPapelMm} mm ({config.LarguraPontos} pontos)")
            .Par("DATA", Data(DateTime.Now))
            .Separador()
            .Linha("ÁÉÍÓÚ ÂÊÔ ÃÕ Ç  áéíóú âêô ãõ ç", Alinhamento.Centro)
            .Linha("Se você consegue ler esta linha, a impressora está funcionando!", Alinhamento.Centro);
}
