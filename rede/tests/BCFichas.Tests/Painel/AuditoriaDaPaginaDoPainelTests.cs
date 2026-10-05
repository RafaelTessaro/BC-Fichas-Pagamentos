using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using BCFichas.Painel;
using BCFichas.Tests.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BCFichas.Tests.Painel;

/// <summary>
/// A página do celular (o JavaScript do index.html) rodando de verdade num navegador sem tela, com o relógio
/// virtual do Chromium. Precisa do Chromium do ambiente de desenvolvimento; sem ele (ex.: CI do Windows), não roda.
/// </summary>
public class AuditoriaDaPaginaDoPainelTests : IAsyncLifetime
{
    private const string Pin = "246810";
    private static readonly string Navegador = "/opt/pw-browsers/chromium_headless_shell-1194/chrome-linux/headless_shell";

    private readonly List<SistemaTemporario> _maquinas = [];
    private readonly List<Servidor> _servidores = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var s in _servidores) await s.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var m in _maquinas) m.Dispose();
    }

    private (Sistema Sistema, SessaoCaixa Sessao, SistemaTemporario Pasta) Maquina(int caixa)
    {
        var t = new SistemaTemporario();
        _maquinas.Add(t);
        var c = t.Sistema.Config.Atual.Clonar();
        c.NomeEvento = "FESTA";
        c.NumeroCaixa = caixa;
        c.PainelAtivo = true;
        c.PainelPin = Pin;
        t.Sistema.Config.Salvar(c);
        return (t.Sistema, t.Sistema.Caixa.Abrir(caixa, null, 0), t);
    }

    private static Pedido Vender(Sistema s, SessaoCaixa sessao, FormaPagamento forma, params (string Nome, int Qtd)[] itens)
    {
        var carrinho = new Carrinho();
        foreach (var (nome, qtd) in itens) carrinho.Adicionar(s.Catalogo.Produtos().First(p => p.Nome == nome), qtd);
        var pedido = s.Vendas.CriarPedido(sessao, carrinho.Linhas, forma, forma == FormaPagamento.Dinheiro ? 100000 : 0);
        return forma == FormaPagamento.Dinheiro ? pedido : s.Vendas.ConfirmarPagamento(pedido.Id, null);
    }

    private async Task<Servidor> Subir(SistemaTemporario t)
    {
        var s = await Servidor.Iniciar(t.Pasta, porta: 0, soLocal: true);
        _servidores.Add(s);
        return s;
    }

    /// <summary>
    /// Abre a página como o celular (com o PIN do QR code), deixa o relógio virtual andar e devolve o HTML da tela.
    /// </summary>
    private static string Tela(Servidor s, string aba, int segundosVirtuais)
    {
        using var p = Process.Start(new ProcessStartInfo(Navegador,
        [
            "--no-sandbox", "--window-size=412,915", $"--virtual-time-budget={segundosVirtuais * 1000}", "--dump-dom",
            $"http://127.0.0.1:{s.Porta}/?pin={Pin}#{aba}",
        ]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        var html = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60000), "o navegador não terminou");
        return WebUtility.HtmlDecode(html.Result);
    }

    [Fact]
    public async Task Porcentagens_das_formas_de_pagamento_somam_100()
    {
        if (!File.Exists(Navegador)) return;
        var (s, sessao, t) = Maquina(1);
        // Três formas com o mesmo valor: 33,3% cada (arredondando cada uma, 33 + 33 + 33 = 99%)
        Vender(s, sessao, FormaPagamento.Dinheiro, ("PASTEL", 1));
        Vender(s, sessao, FormaPagamento.Pix, ("PASTEL", 1));
        Vender(s, sessao, FormaPagamento.Debito, ("PASTEL", 1));
        var servidor = await Subir(t);

        var html = Tela(servidor, "painel", 5);
        var formas = html[html.IndexOf("Formas de pagamento", StringComparison.Ordinal)..];
        formas = formas[..formas.IndexOf("<h2>Máquinas</h2>", StringComparison.Ordinal)];
        var porcentagens = Regex.Matches(formas, @"<small>(\d+)%</small>").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(4, porcentagens.Count);
        Assert.Equal(100, porcentagens.Sum());
    }

    [Fact]
    public async Task Com_devolucao_o_total_mostrado_tem_o_mesmo_nome_do_relatorio_do_caixa()
    {
        if (!File.Exists(Navegador)) return;
        var (s, sessao, t) = Maquina(1);
        var pedido = Vender(s, sessao, FormaPagamento.Dinheiro, ("PASTEL", 2), ("CERVEJA", 1)); // R$ 28
        Vender(s, sessao, FormaPagamento.Pix, ("PASTEL", 1));                                     // R$ 10
        s.Devolucoes.Devolver(sessao, pedido.Id, new Dictionary<long, int> { [s.Vendas.Pedido(pedido.Id)!.Itens[0].Id] = 1 }, "");
        var resumo = s.Caixa.Resumo(sessao.Id);
        Assert.Equal((3800, 1000, 2800), (resumo.TotalVendas, resumo.TotalDevolvido, resumo.VendaLiquida));
        var servidor = await Subir(t);

        // No relatório do caixa, "TOTAL VENDIDO" é R$ 38,00 e "VENDA LÍQUIDA" é R$ 28,00: o celular não pode
        // mostrar R$ 28,00 com o nome de total vendido.
        var html = Tela(servidor, "painel", 5);
        var destaque = Regex.Match(html, @"<div class=""rotulo"">([^<]+)</div>\s*<div class=""total"">([^<]+)</div>");
        Assert.True(destaque.Success, html);
        var (rotulo, valor) = (destaque.Groups[1].Value.Trim(), destaque.Groups[2].Value.Trim());
        if (rotulo == "TOTAL VENDIDO") Assert.Equal("R$ 38,00", valor);
        else
        {
            Assert.Equal(("VENDA LÍQUIDA", "R$ 28,00"), (rotulo, valor));
            Assert.Contains("vendido R$ 38,00", html);
        }
    }

    [Fact]
    public async Task Ultima_venda_continua_contando_o_tempo_quando_nada_muda()
    {
        if (!File.Exists(Navegador)) return;
        var (s, sessao, t) = Maquina(1);
        Vender(s, sessao, FormaPagamento.Pix, ("PASTEL", 1));
        var servidor = await Subir(t);

        // Três minutos com o painel aberto sem tocar em Atualizar: os números não mudam, mas a linha da máquina não
        // pode continuar dizendo "última venda agora".
        var html = Tela(servidor, "maquinas", 200);
        var linha = Regex.Match(html, @"Caixa 01 \(esta\)<small>([^<]+)</small>");
        Assert.True(linha.Success, html);
        Assert.Equal("última venda há 3 min", linha.Groups[1].Value);
    }
}
