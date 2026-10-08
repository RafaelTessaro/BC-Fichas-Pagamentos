using SkiaSharp;

namespace BCFichas.Core.Impressao;

public sealed record ResultadoImpressao(bool Ok, string Mensagem);

/// <summary>Imprime fichas e relatórios no destino configurado (Elgin i9, porta COM ou arquivo).</summary>
public sealed class ServicoImpressao
{
    private readonly Func<Configuracao> _config;
    private readonly string _pastaDados;
    private readonly object _trava = new();
    private (string Caminho, DateTime Data)? _chaveLogo;
    private LogoFicha? _logo;

    public ServicoImpressao(Func<Configuracao> config, string pastaDados)
    {
        _config = config;
        _pastaDados = pastaDados;
    }

    /// <summary>Avisado depois de cada impressão (barra de status).</summary>
    public event Action<ResultadoImpressao>? Impresso;

    public ResultadoImpressao? Ultimo { get; private set; }

    /// <summary>Pasta (dentro da pasta de dados) onde "Salvar em arquivo" guarda as fichas, se não escolher outra.</summary>
    public const string PastaArquivoPadrao = "impressoes";

    public string PastaPadraoArquivo => Path.Combine(_pastaDados, PastaArquivoPadrao);

    /// <summary>Abre a porta COM (os testes trocam por uma impressora de mentira).</summary>
    internal Func<string, int, Stream> AbrirPorta { get; set; } = TransporteSerial.Abrir;

    /// <summary>
    /// Pedidos que pararam no meio da impressão (porta COM): até que ficha já saiu. A próxima impressão do pedido
    /// (Tentar de novo, Fichas não impressas) manda só as que faltam, porque as que saíram já estão com o cliente.
    /// </summary>
    private readonly Dictionary<(int Caixa, long Pedido), int> _jaSairam = new();

    public IDestinoImpressao CriarDestino(Configuracao c) => c.Impressora switch
    {
        TipoImpressora.Serial => new DestinoSerial(c.PortaSerial, () => AbrirPorta(c.PortaSerial, c.BaudRate), c.Corte),
        TipoImpressora.Arquivo => new DestinoArquivo(string.IsNullOrWhiteSpace(c.PastaArquivo) ? PastaPadraoArquivo : c.PastaArquivo),
        _ => new DestinoEscPos(string.IsNullOrWhiteSpace(c.NomeImpressora) ? "Impressora não escolhida" : c.NomeImpressora,
            dados => TransporteWindows.Enviar(c.NomeImpressora, dados), c.Corte),
    };

    public string Descricao() => CriarDestino(_config()).Descricao;

    public ResultadoImpressao Fichas(IReadOnlyList<Ficha> fichas)
    {
        if (fichas.Count == 0) return new ResultadoImpressao(true, "Nada para imprimir.");
        var config = _config();
        lock (_trava)
        {
            var primeira = fichas[0];
            var pedido = (primeira.Caixa, primeira.NumeroPedido);
            // Reimpressão sai inteira (vem marcada REIMPRESSÃO); a primeira impressão pula as que já saíram
            var pular = primeira.Reimpressao ? 0 : _jaSairam.GetValueOrDefault(pedido);
            var faltam = fichas.Where(f => f.Sequencia > pular).ToList();
            if (faltam.Count == 0)
            {
                _jaSairam.Remove(pedido);
                return new ResultadoImpressao(true, "As fichas deste pedido já tinham saído.");
            }
            var resultado = Executar(config, () =>
            {
                var logo = Logo(config);
                return faltam.Select(f => RenderizadorFicha.Renderizar(f, config, logo));
            }, faltam.Count == 1 ? "1 ficha impressa" : $"{faltam.Count} fichas impressas", enviadas =>
            {
                if (primeira.Reimpressao) return null;
                var ate = enviadas > 0 ? faltam[enviadas - 1].Sequencia : pular;
                if (ate == 0) return null;
                _jaSairam[pedido] = ate;
                return $"A impressora parou no meio: saíram as fichas 1 a {ate} de {primeira.TotalFichas}. Entregue essas " +
                       "ao cliente (as que ficaram dentro da impressora saem quando trocar o papel). Ao tentar de novo, " +
                       "só saem as que faltam.";
            });
            if (resultado.Ok) _jaSairam.Remove(pedido);
            return resultado;
        }
    }

    public ResultadoImpressao Documento(Documento documento, string descricao = "Relatório impresso")
    {
        var config = _config();
        return Executar(config, () => [documento.Renderizar(config.LarguraConteudo, config.Escala)], descricao);
    }

    public ResultadoImpressao Teste(Configuracao config)
    {
        var destino = CriarDestino(config);
        return Executar(config, () =>
        {
            var logo = Logo(config);
            return
            [
                Relatorios.Teste(config, destino.Descricao).Renderizar(config.LarguraConteudo, config.Escala),
                RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, logo),
            ];
        }, "Teste impresso");
    }

    /// <summary>Imagem da ficha em preto e branco, como vai sair no papel (prévia na tela).</summary>
    public SKBitmap Previa(Configuracao config) => PreviaSeLivre(config, Timeout.Infinite)!;

    /// <summary>
    /// A prévia, se a impressão não estiver ocupada: com a porta COM esperando a impressora (até 15 s), a tela das
    /// Configurações não pode ficar parada esperando junto. Ocupada: devolve nulo (a tela tenta de novo depois).
    /// </summary>
    public SKBitmap? PreviaSeLivre(Configuracao config, int esperaMs = 50)
    {
        if (!Monitor.TryEnter(_trava, esperaMs)) return null;
        try
        {
            using var colorida = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, Logo(config));
            return ImagemUtil.Monocromatico(colorida);
        }
        finally
        {
            Monitor.Exit(_trava);
        }
    }

    /// <summary>Caminho absoluto de uma imagem guardada na pasta de dados.</summary>
    public string? CaminhoImagem(string? relativo) =>
        string.IsNullOrWhiteSpace(relativo) ? null
        : Path.IsPathRooted(relativo) ? relativo
        : Path.Combine(_pastaDados, relativo);

    /// <param name="paginas">
    /// Desenha as páginas sob demanda: cada ficha é desenhada, mandada e solta antes da próxima (um pedido com 50
    /// fichas não junta 50 imagens de ~1 MB na memória do tablet).
    /// </param>
    /// <param name="noMeio">
    /// A impressora parou depois de algumas páginas (porta COM): recebe quantas foram inteiras e devolve o aviso.
    /// </param>
    private ResultadoImpressao Executar(Configuracao config, Func<IEnumerable<SKBitmap>> paginas, string sucesso,
        Func<int, string?>? noMeio = null)
    {
        ResultadoImpressao resultado;
        lock (_trava)
        {
            try
            {
                CriarDestino(config).Imprimir(UmaDeCadaVez(paginas(), config));
                resultado = new ResultadoImpressao(true, sucesso);
            }
            catch (ErroDeNegocio e)
            {
                resultado = new ResultadoImpressao(false, e.Message);
            }
            catch (FalhaNoMeio e)
            {
                resultado = new ResultadoImpressao(false, noMeio?.Invoke(e.Enviadas) ?? "Erro na impressora: " + e.Message);
            }
            catch (Exception e)
            {
                resultado = new ResultadoImpressao(false, "Erro na impressora: " + e.Message);
            }
        }

        Ultimo = resultado;
        Impresso?.Invoke(resultado);
        return resultado;
    }

    /// <summary>Entrega uma página por vez, já na posição do papel, e solta cada uma quando o destino passa adiante.</summary>
    private static IEnumerable<SKBitmap> UmaDeCadaVez(IEnumerable<SKBitmap> desenhos, Configuracao config)
    {
        foreach (var desenho in desenhos)
        {
            var pagina = desenho;
            if (config.AjusteHorizontal != 0)
            {
                try
                {
                    pagina = Posicionar(desenho, config);
                }
                finally
                {
                    desenho.Dispose();
                }
            }
            try
            {
                yield return pagina;
            }
            finally
            {
                pagina.Dispose();
            }
        }
    }

    /// <summary>Põe o desenho na linha da impressora, puxado para o lado do ajuste horizontal.</summary>
    internal static SKBitmap Posicionar(SKBitmap desenho, Configuracao config)
    {
        var linha = new SKBitmap(new SKImageInfo(config.LarguraPontos, desenho.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(linha);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(desenho, config.InicioConteudo, 0);
        canvas.Flush();
        return linha;
    }

    /// <summary>Logo do evento (carregado uma vez só). Chamar sempre dentro de <see cref="_trava"/>.</summary>
    private LogoFicha? Logo(Configuracao config)
    {
        var caminho = CaminhoImagem(config.Logo);
        if (caminho is null || !File.Exists(caminho)) return null;

        var chave = (caminho, File.GetLastWriteTimeUtc(caminho));
        if (_chaveLogo == chave) return _logo;

        _logo?.Dispose();
        _logo = null;
        _chaveLogo = chave;
        var original = ImagemUtil.Carregar(caminho);
        if (original is null) return null;
        _logo = new LogoFicha(original);
        return _logo;
    }
}
