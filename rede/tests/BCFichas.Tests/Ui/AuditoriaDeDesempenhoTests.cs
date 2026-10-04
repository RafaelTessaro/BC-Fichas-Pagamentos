using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Tests.Core;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace BCFichas.Tests.Ui;

/// <summary>
/// Auditoria de memória e processador para o tablet (1 GB de RAM, Atom de 4 núcleos fracos, o dia inteiro ligado):
/// guarda as correções feitas depois de medir um dia inteiro de vendas (3.000 vendas imprimindo), as telas abertas
/// centenas de vezes e o banco com 50 mil pedidos. Nenhuma tela, diálogo ou venda deixou memória presa; o que pesava
/// era a impressão (cópias grandes a cada ficha), a troca de aba e imagens soltas só pelo coletor.
/// </summary>
public class AuditoriaDeDesempenhoTests(ITestOutputHelper saida)
{
    /// <summary>
    /// A ficha virava ESC/POS (e PNG no "Salvar em arquivo") passando por cópias do tamanho da ficha inteira: ~400 KB
    /// por ficha na impressora e ~1 MB no arquivo, na área de objetos grandes, e o coletor parava o programa (coleta
    /// completa) a cada poucas fichas. Agora lê os pontos direto da imagem, e os bytes continuam exatamente iguais.
    /// </summary>
    [Fact]
    public void Ficha_vira_ESC_POS_e_preto_e_branco_igual_antes_sem_copiar_a_ficha_inteira()
    {
        using var temporario = new SistemaTemporario();
        var config = temporario.Sistema.Config.Atual.Clonar();
        var imagens = new List<SKBitmap>();
        foreach (var (modelo, _) in RenderizadorFicha.Modelos)
        {
            config.Modelo = modelo;
            config.Moldura = modelo == ModeloFicha.Destaque;
            imagens.Add(RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, null));
        }
        // Formato diferente (BGRA, sem premultiplicar) e com transparência e cinzas perto do limite do preto
        var outra = new SKBitmap(new SKImageInfo(203, 61, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        for (var y = 0; y < outra.Height; y++)
        for (var x = 0; x < outra.Width; x++)
            outra.SetPixel(x, y, new SKColor((byte)(x * 7), (byte)(140 + y % 20), (byte)(y * 13), (byte)(x * y % 256)));
        imagens.Add(outra);

        foreach (var imagem in imagens)
        {
            Assert.Equal(EscPosAntigo(imagem), EscPos.Imagem(imagem));
            using var mono = ImagemUtil.Monocromatico(imagem);
            Assert.Equal(MonocromaticoAntigo(imagem), mono.GetPixelSpan().ToArray());
        }
        Assert.Equal(TrabalhoAntigo(imagens, TipoCorte.Parcial), EscPos.Trabalho(imagens, TipoCorte.Parcial));
        Assert.Equal(TrabalhoAntigo(imagens, TipoCorte.Nenhum), EscPos.Trabalho(imagens, TipoCorte.Nenhum));

        // Memória: só os bytes da impressora (1 bit por ponto) e uma linha por vez
        var ficha = imagens[0];
        var bits = (ficha.Width + 7) / 8 * ficha.Height;
        EscPos.Imagem(ficha);
        ImagemUtil.Monocromatico(ficha).Dispose();
        var antes = GC.GetAllocatedBytesForCurrentThread();
        EscPos.Imagem(ficha);
        var escpos = GC.GetAllocatedBytesForCurrentThread() - antes;
        antes = GC.GetAllocatedBytesForCurrentThread();
        ImagemUtil.Monocromatico(ficha).Dispose();
        var monocromatico = GC.GetAllocatedBytesForCurrentThread() - antes;
        saida.WriteLine($"Ficha {ficha.Width}x{ficha.Height}: ESC/POS alocou {escpos / 1024} KB, preto e branco {monocromatico / 1024} KB " +
                        $"(antes: {(ficha.Width * ficha.Height + 2 * bits) / 1024} KB e {ficha.Width * ficha.Height * 5 / 1024} KB)");
        Assert.True(escpos < 2 * bits + 16 * 1024, $"ESC/POS alocou {escpos} bytes");
        Assert.True(monocromatico < ficha.Width * 4 + 16 * 1024, $"preto e branco alocou {monocromatico} bytes");
        foreach (var imagem in imagens) imagem.Dispose();
    }

    // O jeito antigo (com a cópia em tons de cinza), para conferir que os bytes não mudaram.
    private static byte[] LuminanciaAntiga(SKBitmap bitmap)
    {
        using var rgba = bitmap.ColorType == SKColorType.Rgba8888 && bitmap.AlphaType == SKAlphaType.Premul
            ? null
            : bitmap.Copy(SKColorType.Rgba8888);
        var fonte = rgba ?? bitmap;
        var pixels = fonte.GetPixelSpan();
        var resultado = new byte[fonte.Width * fonte.Height];
        for (int i = 0, p = 0; i < resultado.Length; i++, p += 4)
        {
            int a = pixels[p + 3];
            int r = pixels[p] + (255 - a);
            int g = pixels[p + 1] + (255 - a);
            int b = pixels[p + 2] + (255 - a);
            resultado[i] = (byte)Math.Clamp((r * 299 + g * 587 + b * 114) / 1000, 0, 255);
        }
        return resultado;
    }

    private static byte[] EscPosAntigo(SKBitmap bitmap)
    {
        var largura = bitmap.Width;
        var altura = bitmap.Height;
        var bytesPorLinha = (largura + 7) / 8;
        var lum = LuminanciaAntiga(bitmap);
        using var saida = new MemoryStream();
        for (var inicio = 0; inicio < altura; inicio += 128)
        {
            var linhas = Math.Min(128, altura - inicio);
            saida.Write([0x1D, 0x76, 0x30, 0x00, (byte)(bytesPorLinha & 0xFF), (byte)(bytesPorLinha >> 8),
                (byte)(linhas & 0xFF), (byte)(linhas >> 8)]);
            for (var y = inicio; y < inicio + linhas; y++)
            {
                var linha = new byte[bytesPorLinha];
                for (var x = 0; x < largura; x++)
                    if (lum[y * largura + x] < ImagemUtil.Limiar)
                        linha[x >> 3] |= (byte)(0x80 >> (x & 7));
                saida.Write(linha);
            }
        }
        return saida.ToArray();
    }

    private static byte[] TrabalhoAntigo(IEnumerable<SKBitmap> paginas, TipoCorte corte)
    {
        using var saida = new MemoryStream();
        saida.Write(EscPos.Inicializar());
        foreach (var pagina in paginas)
        {
            saida.Write(EscPosAntigo(pagina));
            if (corte == TipoCorte.Nenhum)
            {
                saida.Write(EscPos.Avancar(5));
                continue;
            }
            saida.Write(EscPos.AvancarPontos(12));
            saida.Write(EscPos.Cortar(parcial: corte == TipoCorte.Parcial));
        }
        return saida.ToArray();
    }

    private static byte[] MonocromaticoAntigo(SKBitmap origem)
    {
        var lum = LuminanciaAntiga(origem);
        var saida = new byte[lum.Length * 4];
        for (var i = 0; i < lum.Length; i++)
        {
            var v = (byte)(lum[i] < ImagemUtil.Limiar ? 0 : 255);
            saida[i * 4] = saida[i * 4 + 1] = saida[i * 4 + 2] = v;
            saida[i * 4 + 3] = 255;
        }
        return saida;
    }

    /// <summary>
    /// Trocar de aba desmontava a grade inteira (botão, foto, textos e estilos de cada produto) e montava de novo.
    /// Agora os botões que já estão na tela mostram os produtos da outra aba: a troca ficou cerca de 2x mais rápida
    /// e a memória do processo não incha com as grades jogadas fora.
    /// </summary>
    [AvaloniaFact]
    public void Trocar_de_aba_usa_os_mesmos_botoes_da_tela()
    {
        using var t = new TelaDeTeste();
        t.AbrirCaixa();
        var comidas = t.Venda.Abas.Single(a => a.Nome == "COMIDAS");
        var bebidas = t.Venda.Abas.Single(a => a.Nome == "BEBIDAS");
        var doces = t.Venda.Abas.Single(a => a.Nome == "DOCES");
        var botoes = t.Venda.Botoes.ToList();
        var telas = t.Janela.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Where(b => b.Classes.Contains("produto")).ToList();
        Assert.Equal(6, botoes.Count);

        // Bebidas: 5 produtos nos mesmos 5 primeiros botões (o sexto sai), com o nome, o preço e a cor de cada um
        t.Venda.SelecionarAbaCommand.Execute(bebidas);
        TelaDeTeste.Atualizar();
        Assert.Equal(botoes.Take(5), t.Venda.Botoes);
        Assert.Equal(["REFRIGERANTE", "ÁGUA", "SUCO", "CERVEJA", "QUENTÃO"], t.Venda.Botoes.Select(b => b.Nome));
        Assert.Equal("R$ 6,00", t.Venda.Botoes[0].Preco);
        Assert.Equal(Avalonia.Media.Color.Parse("#1971C2"), ((Avalonia.Media.ISolidColorBrush)t.Venda.Botoes[0].Fundo).Color);
        Assert.Equal("LATA", t.Venda.Botoes[0].Detalhe);
        var naTela = t.Janela.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Where(b => b.Classes.Contains("produto")).ToList();
        Assert.Equal(telas.Take(5), naTela);
        Assert.Contains(t.Janela.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(),
            x => x.Text == "REFRIGERANTE" && x.IsEffectivelyVisible);
        Assert.DoesNotContain(t.Janela.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(),
            x => x.Text == "PASTEL" && x.IsEffectivelyVisible);

        // Tocar no botão reaproveitado vende o produto que ele mostra agora
        t.Tocar("SUCO");
        Assert.Equal("SUCO", t.Venda.Linhas.Single().Nome);

        // Volta para uma aba com mais produtos: os 5 continuam e só entra um botão novo
        t.Venda.SelecionarAbaCommand.Execute(doces);
        t.Venda.SelecionarAbaCommand.Execute(comidas);
        TelaDeTeste.Atualizar();
        Assert.Equal(botoes.Take(4), t.Venda.Botoes.Take(4));
        Assert.Equal(["PASTEL", "ESPETINHO", "CACHORRO-QUENTE", "PORÇÃO DE BATATA", "PIZZA (FATIA)", "CALDO"],
            t.Venda.Botoes.Select(b => b.Nome));
        Assert.Equal("CARNE OU QUEIJO", t.Venda.Botoes[0].Detalhe);
        Assert.False(t.Venda.Botoes[5].Esgotado);
    }

    /// <summary>
    /// Ao abrir, o programa procura os pedidos que ficaram esperando a maquininha. Isso lia a tabela de pedidos
    /// inteira (todas as festas guardadas: 2,5 ms com 50 mil pedidos no PC, bem mais no tablet com o disco frio).
    /// </summary>
    [Fact]
    public void Pedidos_pendentes_sao_achados_sem_ler_todos_os_pedidos()
    {
        using var temporario = new SistemaTemporario();
        var s = temporario.Sistema;
        var plano = string.Join(" | ", s.Banco.Consultar(
            $"EXPLAIN QUERY PLAN SELECT id FROM pedidos WHERE status = {(int)StatusPedido.AguardandoPagamento} ORDER BY id",
            l => l.GetString(3)));
        Assert.Contains("ix_pedidos_pendentes", plano);

        // Os mesmos pedidos de antes: só os que ficaram esperando a maquininha, de verdade ou de teste, em ordem
        MuitasVendas.Criar(s, dias: 2, pedidosPorDia: 50);
        var produto = s.Catalogo.Produtos()[0];
        var real = s.Caixa.Abrir(1, null, 0);
        var pendente = s.Vendas.CriarPedido(real, [new() { Produto = produto, Quantidade = 1 }], FormaPagamento.Pix);
        s.Vendas.CriarPedido(real, [new() { Produto = produto, Quantidade = 1 }], FormaPagamento.Dinheiro, 99999);
        var pago = s.Vendas.CriarPedido(real, [new() { Produto = produto, Quantidade = 1 }], FormaPagamento.Debito);
        s.Vendas.ConfirmarPagamento(pago.Id, null);
        var teste = s.Caixa.Abrir(1, null, 0, teste: true);
        var pendenteTeste = s.Vendas.CriarPedido(teste, [new() { Produto = produto, Quantidade = 2 }], FormaPagamento.Credito);
        Assert.Equal([pendente.Id, pendenteTeste.Id], s.Vendas.Pendentes().Select(p => p.Id));
        Assert.All(s.Vendas.Pendentes(), p => Assert.Equal(StatusPedido.AguardandoPagamento, p.Status));
        s.Vendas.Cancelar(pendente.Id);
        Assert.Equal([pendenteTeste.Id], s.Vendas.Pendentes().Select(p => p.Id));

        // Banco de uma versão anterior (sem o índice): ganha o índice ao abrir
        s.Banco.Executar("DROP INDEX ix_pedidos_pendentes");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var aberto = Sistema.Iniciar(temporario.Pasta, criarExemplos: false);
        Assert.Equal(1, aberto.Banco.Escalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_pedidos_pendentes'"));
        Assert.Equal([pendenteTeste.Id], aberto.Vendas.Pendentes().Select(p => p.Id));
    }

    /// <summary>
    /// A prévia da ficha das configurações (e o QR code do painel) são imagens fora da área do coletor. Ao sair da
    /// tela elas ficavam presas até o coletor passar (com menos coletas completas, isso demoraria cada vez mais).
    /// </summary>
    [AvaloniaFact]
    public void Sair_das_configuracoes_solta_a_previa_da_ficha_na_hora()
    {
        // Com o painel no celular ligado a tela também mostra o QR code (outra imagem)
        BCFichas.App.PainelDaRede.Enderecos = () => [System.Net.IPAddress.Parse("192.168.1.10")];
        using var t = new TelaDeTeste(configurar: c =>
        {
            c.PainelAtivo = true;
            c.PainelPin = "4321";
        });
        t.AbrirCaixa();
        var config = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(config);
        TelaDeTeste.Atualizar();
        var previa = Assert.IsType<Avalonia.Media.Imaging.Bitmap>(config.Previa);
        var qr = Assert.IsType<Avalonia.Media.Imaging.Bitmap>(config.PainelQr);
        Assert.True(previa.PixelSize.Width > 0 && qr.PixelSize.Width > 0);
        config.NomeEvento = "OUTRA FESTA"; // a prévia nova ainda ia ser desenhada depois de sair

        t.Principal.IrParaVenda();
        TelaDeTeste.Atualizar();
        Assert.IsType<VendaViewModel>(t.Principal.Pagina);
        Assert.Null(config.Previa);
        Assert.Null(config.PainelQr);
        Assert.ThrowsAny<Exception>(() => previa.PixelSize); // solta (Dispose): não dá mais para ler
        Assert.ThrowsAny<Exception>(() => qr.PixelSize);
        Thread.Sleep(300);
        TelaDeTeste.Atualizar();
        Assert.Null(config.Previa);

        // Abrir de novo desenha a prévia de novo
        var outra = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(outra);
        TelaDeTeste.Atualizar();
        Assert.NotNull(outra.Previa);
    }

    /// <summary>Telas e diálogos abertos e fechados várias vezes saem da memória (nenhum evento ou timer os segura).</summary>
    [AvaloniaFact]
    public async Task Telas_e_dialogos_abertos_muitas_vezes_saem_da_memoria()
    {
        using var t = new TelaDeTeste(configurar: c =>
        {
            c.LiberarDevolucao = true;
            c.LiberarReimpressao = true;
        });
        t.AbrirCaixa();
        await VenderEmDinheiro(t, [t.Venda.Botoes[0]]);
        var vivos = new List<(string Nome, WeakReference Referencia)>();
        void Guardar(object? o)
        {
            if (o is not null) vivos.Add((o.GetType().Name, new WeakReference(o)));
        }

        for (var volta = 0; volta < 4; volta++)
        {
            t.Venda.AbrirMenuCommand.Execute(null);
            TelaDeTeste.Atualizar();
            Guardar(t.Principal.Dialogo);
            t.Principal.FecharTodosDialogos();
            foreach (var pagina in new PaginaViewModel[]
                     {
                         new ProdutosViewModel(t.Principal), new ConfiguracaoViewModel(t.Principal),
                         new RelatoriosViewModel(t.Principal), new ReimpressaoViewModel(t.Principal),
                         new DevolucaoViewModel(t.Principal), new FechamentoViewModel(t.Principal),
                         new SangriaViewModel(t.Principal),
                     })
            {
                t.Principal.Abrir(pagina);
                TelaDeTeste.Atualizar();
                Guardar(pagina);
                Guardar(t.Janela.GetVisualDescendants().OfType<Avalonia.Controls.UserControl>()
                    .FirstOrDefault(c => c.DataContext == pagina));
                t.Principal.IrParaVenda();
                TelaDeTeste.Atualizar();
            }
            t.Tocar("PASTEL");
            t.Venda.PagarCommand.Execute(null);
            TelaDeTeste.Atualizar();
            Guardar(t.Principal.Dialogo);
            ((PagamentoViewModel)t.Principal.Dialogo!).FecharCommand.Execute(null);
            t.Venda.LimparPedido();
            TelaDeTeste.Atualizar();
        }

        HeapDepoisDeColetar();
        var presos = vivos.Where(v => v.Referencia.IsAlive).GroupBy(v => v.Nome)
            .Select(g => $"{g.Key} ({g.Count()})").ToList();
        saida.WriteLine($"{vivos.Count} telas e diálogos abertos; ainda na memória: {string.Join(", ", presos)}");
        // A última de cada tipo pode ainda estar guardada pelo Avalonia (foco, último quadro): as outras não.
        Assert.All(vivos.Where(v => v.Referencia.IsAlive).GroupBy(v => v.Nome), g => Assert.True(g.Count() <= 1, g.Key));
    }

    // ---------- Apoio ----------

    private static void HeapDepoisDeColetar()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task VenderEmDinheiro(TelaDeTeste t, IEnumerable<BotaoProduto> botoes)
    {
        foreach (var b in botoes)
        {
            t.Venda.AdicionarCommand.Execute(b);
            TelaDeTeste.Atualizar();
        }
        t.Venda.PagarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        var pagamento = Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
        pagamento.EscolherDinheiroCommand.Execute(null);
        await pagamento.ConfirmarDinheiroCommand.ExecuteAsync(null);
        pagamento.FecharCommand.Execute(null);
        TelaDeTeste.Atualizar();
    }
}
