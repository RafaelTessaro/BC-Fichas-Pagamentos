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

    public IDestinoImpressao CriarDestino(Configuracao c) => c.Impressora switch
    {
        TipoImpressora.Serial => new DestinoEscPos($"Porta {c.PortaSerial}",
            dados => TransporteSerial.Enviar(c.PortaSerial, c.BaudRate, dados), c.Corte),
        TipoImpressora.Arquivo => new DestinoArquivo(string.IsNullOrWhiteSpace(c.PastaArquivo) ? PastaPadraoArquivo : c.PastaArquivo),
        _ => new DestinoEscPos(string.IsNullOrWhiteSpace(c.NomeImpressora) ? "Impressora não escolhida" : c.NomeImpressora,
            dados => TransporteWindows.Enviar(c.NomeImpressora, dados), c.Corte),
    };

    public string Descricao() => CriarDestino(_config()).Descricao;

    public ResultadoImpressao Fichas(IReadOnlyList<Ficha> fichas)
    {
        if (fichas.Count == 0) return new ResultadoImpressao(true, "Nada para imprimir.");
        var config = _config();
        return Executar(config, () =>
        {
            var logo = Logo(config);
            return fichas.Select(f => RenderizadorFicha.Renderizar(f, config, logo)).ToList();
        }, fichas.Count == 1 ? "1 ficha impressa" : $"{fichas.Count} fichas impressas");
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
    public SKBitmap Previa(Configuracao config)
    {
        lock (_trava)
        {
            using var colorida = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, Logo(config));
            return ImagemUtil.Monocromatico(colorida);
        }
    }

    /// <summary>Caminho absoluto de uma imagem guardada na pasta de dados.</summary>
    public string? CaminhoImagem(string? relativo) =>
        string.IsNullOrWhiteSpace(relativo) ? null
        : Path.IsPathRooted(relativo) ? relativo
        : Path.Combine(_pastaDados, relativo);

    private ResultadoImpressao Executar(Configuracao config, Func<List<SKBitmap>> paginas, string sucesso)
    {
        ResultadoImpressao resultado;
        lock (_trava)
        {
            List<SKBitmap>? lista = null;
            try
            {
                lista = paginas();
                if (config.AjusteHorizontal != 0)
                {
                    var desenhos = lista;
                    lista = desenhos.Select(p => Posicionar(p, config)).ToList();
                    foreach (var d in desenhos) d.Dispose();
                }
                CriarDestino(config).Imprimir(lista);
                resultado = new ResultadoImpressao(true, sucesso);
            }
            catch (ErroDeNegocio e)
            {
                resultado = new ResultadoImpressao(false, e.Message);
            }
            catch (Exception e)
            {
                resultado = new ResultadoImpressao(false, "Erro na impressora: " + e.Message);
            }
            finally
            {
                if (lista is not null)
                    foreach (var p in lista) p.Dispose();
            }
        }

        Ultimo = resultado;
        Impresso?.Invoke(resultado);
        return resultado;
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
